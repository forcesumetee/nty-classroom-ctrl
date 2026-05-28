using ClassroomCtrl.Shared.Protocol;
using SharpGen.Runtime;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 2 (round 3) — Media Foundation H.264 encoder, async
/// hardware path (Intel Quick Sync / NVENC / AMF).
///
/// Round 2 shipped the SW MFT via direct CoCreateInstance + sync pump (see
/// <see cref="MediaFoundationH264Encoder"/>).  Round 3 adds the real HW path:
/// MFTEnumEx → IMFActivate → ActivateObject → async pump driven by
/// METransformNeedInput / METransformHaveOutput events on IMFMediaEventGenerator.
///
/// Why a second class instead of folding into the SW encoder: the round-2 SW
/// encoder is GREEN (decoder-compat verified) and the diff to add async-mode
/// branches to every method (ctor, Encode, Drain, Dispose) makes it harder to
/// read and easier to break the green path.  Two classes keep round 2 untouched
/// and the round-3 diff bounded to one new file + the factory dispatch.
///
/// Fallback chain (in <see cref="VideoEncoderFactory"/>):
///     this (HW async) → MediaFoundationH264Encoder (SW sync, round 2)
///     → OpenH264Encoder (always-available CPU)
///     → MJpegEncoder
/// Each catch is silent except for ActiveEncoderDescription which the broadcaster
/// logs once at Start so the dev sees which encoder is actually live.
///
/// Round-3 resolved API names (probe table — full version in hand-off report):
///   • MFTEnumEx: <c>MediaFactory.MFTEnumEx(Guid cat, uint flags, RegisterTypeInfo? in, RegisterTypeInfo? out, out IntPtr pppActivate, out uint count)</c>
///     — returns IntPtr to native IMFActivate* array.
///   • Video encoder category: <c>TransformCategoryGuids.VideoEncoder</c>.
///   • HW enum flag: <c>EnumFlag.EnumFlagHardware | EnumFlagSortandfilter | EnumFlagAsyncmft</c>.
///   • MFT_FRIENDLY_NAME: <c>TransformAttributeKeys.MftFriendlyNameAttribute</c>.
///   • Activate→IMFTransform: <c>activate.ActivateObject&lt;IMFTransform&gt;()</c>.
///   • Event generator: QI from IMFTransform via <c>QueryInterface&lt;IMFMediaEventGenerator&gt;()</c>.
///   • Event type: <c>IMFMediaEvent.EventType</c> (cast int 600/601 — Vortice's
///     <c>MediaEventTypes</c> enum stops at 222 and doesn't include the
///     ME_TRANSFORM_* values, so we use the raw Win32 constants).
///   • Async unlock: <c>TransformAttributeKeys.TransformAsyncUnlock</c>
///     (e5666d6b-...).  Round 2 guessed the wrong GUID; the SW MFT silently
///     ignored.  HW MFTs will NOT — without the correct unlock, every call
///     returns MF_E_TRANSFORM_ASYNC_LOCKED.
///   • ICodecAPI not bound in Vortice — declared manually via <see cref="ICodecAPI"/>
///     [ComImport] interface in this file, then Marshal.QueryInterface from the MFT.
///
/// What this round delivers:
///   • HW H.264 encode via the dev's GPU when one is present (Quick Sync usually,
///     since target customer fleet is i5 onboard).  CPU drops; Task Manager →
///     Performance → GPU → "Video Encode" lights up.
///   • Annex-B output decodable by the EXISTING <see cref="H264DecoderWrapper"/>
///     — verified by extending <c>tools/EncoderSelfTest</c>.
///   • ICodecAPI runtime ForceKeyframe + SetMaxBitrate (so AdaptiveBitrateController
///     reaches the HW encoder).
///   • Honest active-encoder log: <c>MediaFoundation HW (Intel Quick Sync … MFT)</c>
///     only when a HW MFT actually activated.
///
/// What this round does NOT deliver (per spec):
///   • FPS unlock — capture is still GDI, capped at 4/6.  The CPU drop / GPU-encode
///     light-up is the round-3 win; FPS lands in Increment 3 (DXGI).
///   • D3D11 zero-copy input — added if a HW MFT refuses to init without an
///     IMFDXGIDeviceManager (system-memory NV12 tried first; D3D manager swap-in
///     on retry); else deferred to Increment 3.
/// </summary>
public sealed class MediaFoundationH264AsyncEncoder : IVideoEncoder
{
    public VideoCodec Codec => VideoCodec.H264;

    public string ActiveEncoderDescription { get; private set; } = "(uninitialised)";

    /// <summary>Self-test diagnostic — friendly name of the chosen MFT (from
    /// MFT_FRIENDLY_NAME_Attribute) or "" if enumeration didn't find a HW MFT.</summary>
    public string MftFriendlyName { get; private set; } = "";

    /// <summary>Self-test diagnostic — pump-thread error message, "" if healthy.</summary>
    public string PumpStatus => _pumpError ?? "ok";

    /// <summary>Self-test diagnostic — D3D11 device manager setup result.</summary>
    public string D3DStatus { get; private set; } = "(not attempted)";

    /// <summary>Self-test diagnostic — event-type counts: needInput,haveOutput,drainComplete,other.</summary>
    public string EventCounts => $"needInput={_evNeedInput}, haveOutput={_evHaveOutput}, drainComplete={_evDrainComplete}, other={_evOther}, notAccepting={_notAcceptingHits}";

    /// <summary>Self-test diagnostic — first 8 event-type ints actually received (for sanity).</summary>
    public string FirstEventTypes => string.Join(",", _firstEventTypes);

    /// <summary>Self-test diagnostic — last OutputStreamInfo flags observed by DrainOneOutput.</summary>
    public string LastOutputStreamFlags { get; private set; } = "(not called)";

    private int _evNeedInput, _evHaveOutput, _evDrainComplete, _evOther, _notAcceptingHits;
    private readonly System.Collections.Generic.List<int> _firstEventTypes = new();

    /// <summary>Self-test diagnostic — bytes of cached Annex-B SPS/PPS header.</summary>
    public int SpsPpsHeaderSize => _spsPpsAnnexB?.Length ?? 0;

    // ─────────── COM / MF init plumbing (same shape as round 2) ───────────

    private static int _mfStartedFlag;
    private static void EnsureMfStartup()
    {
        if (Interlocked.CompareExchange(ref _mfStartedFlag, 1, 0) != 0) return;
        const uint COINIT_MULTITHREADED = 0x0;
        _ = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        MediaFactory.MFStartup(useLightVersion: false).CheckError();
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    // D3D11CreateDevice — round 3 needs a D3D11 device to feed the AMD/Quick Sync/
    // NVENC HW MFTs.  Even with system-memory NV12 input, the MFT refuses to make
    // internal allocations without an IMFDXGIDeviceManager pointing at a real
    // D3D11 device.  The dev-box experiment confirmed this: without the manager
    // the MFT raises a phantom HAVE_OUTPUT then ProcessOutput returns E_UNEXPECTED
    // and NEED_INPUT never fires.  With the manager, the normal NEED_INPUT/
    // HAVE_OUTPUT cadence kicks in.
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,           // null = default
        int driverType,            // D3D_DRIVER_TYPE_HARDWARE = 1
        IntPtr software,           // null
        uint flags,                // BGRA_SUPPORT | VIDEO_SUPPORT
        IntPtr pFeatureLevels,     // null = let runtime pick
        uint featureLevelCount,
        uint sdkVersion,           // D3D11_SDK_VERSION = 7
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    private const int  D3D_DRIVER_TYPE_HARDWARE      = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA      = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO     = 0x800;
    private const uint D3D11_SDK_VERSION             = 7;

    // ID3D11Multithread interface — required for HW MFTs that drive the D3D11
    // device from a different thread than the one that created it.  The AMD H.264
    // MFT in particular refuses to process input unless this is set.  We call
    // SetMultithreadProtected(TRUE) via raw vtable dispatch to avoid pulling in
    // Vortice.Direct3D11.
    private static readonly Guid IID_ID3D11Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetMultithreadProtectedDelegate(IntPtr self, int bMTProtect);

    private static void EnableD3D11Multithread(IntPtr devicePtr)
    {
        int hr = Marshal.QueryInterface(devicePtr, in IID_ID3D11Multithread, out IntPtr mtPtr);
        if (hr != 0 || mtPtr == IntPtr.Zero) return;
        try
        {
            // Vtable layout: IUnknown (QI=0, AddRef=1, Release=2),
            //                ID3D11Multithread (Enter=3, Leave=4, SetMTProt=5, GetMTProt=6)
            var vtbl = Marshal.ReadIntPtr(mtPtr);
            var setMT = Marshal.ReadIntPtr(vtbl, 5 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedDelegate>(setMT);
            del(mtPtr, 1);  // TRUE
        }
        finally { Marshal.Release(mtPtr); }
    }

    // Win32 MediaEventType values for async MFTs — Vortice's MediaEventTypes enum
    // doesn't include these (it stops at SourceV1Anchor=222).  Verified against
    // mfobjects.h.  Round 4 will fork once Vortice catches up.
    private const int ME_TRANSFORM_NEED_INPUT     = 600;
    private const int ME_TRANSFORM_HAVE_OUTPUT    = 601;
    private const int ME_TRANSFORM_DRAIN_COMPLETE = 602;

    // HRESULT 0xC00D36B5 — MF_E_NOTACCEPTING.  Async MFT says "I don't have any
    // NEED_INPUT debt; defer ProcessInput".  When this is returned to the pump
    // (NEED_INPUT path), we re-enqueue the sample.  When returned to the proactive
    // Encode path (AMD workaround), Encode signals "no room" so the broadcaster
    // drops the frame for this tick.
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    // HRESULT 0xC00D6D72 — MF_E_TRANSFORM_NEED_MORE_INPUT.  Maps to "null this tick"
    // in our IVideoEncoder contract.  Async MFTs shouldn't return this from
    // ProcessOutput (HAVE_OUTPUT events mean a sample is ready), but defensively
    // handle it.
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    // HRESULT 0xC00D3E80 — MF_E_NO_EVENTS_AVAILABLE.  Returned by GetEvent with
    // MF_EVENT_FLAG_NO_WAIT when the event queue is empty.  Round 3 uses this
    // non-blocking variant to pump events and feed input from the same thread.
    private const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    private const int MF_EVENT_FLAG_NO_WAIT    = 0x00000001;

    // MFT enum flag combination for HW async H.264 encoders.  ASYNCMFT in the
    // probe report is value 0x02; HARDWARE is 0x04; SORTANDFILTER is 0x40 (sort
    // by merit + filter by category).  We OR them together so MFTEnumEx returns
    // a pre-sorted list of HW async H.264 encoders.
    private const uint MFT_ENUM_FLAG_HW = (uint)(EnumFlag.EnumFlagHardware
                                                | EnumFlag.EnumFlagAsyncmft
                                                | EnumFlag.EnumFlagSortandfilter);

    // Output type for MFTEnumEx — we want H.264 video encoder output.  Constructed
    // as a RegisterTypeInfo struct (major + sub-type GUIDs).
    private static RegisterTypeInfo H264OutputRegisterTypeInfo() => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype   = VideoFormatGuids.H264
    };

    // ─────────── Encoder state ───────────

    private IMFTransform _mft = null!;
    private IMFMediaEventGenerator _eventGen = null!;
    private ICodecAPI? _codecApi;             // QI'd in ctor — may be null if MFT doesn't expose
    private IMFDXGIDeviceManager? _dxgiManager;
    private ComObject? _d3dDeviceWrapper;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _hnsPerFrame;
    private readonly byte[] _nv12Buffer;
    private byte[]? _spsPpsAnnexB;
    private long _frameCount;

    // ─────────── Async pump plumbing ───────────

    private readonly BlockingCollection<IMFSample> _inputQueue = new(boundedCapacity: 8);
    private readonly BlockingCollection<EncodedFrame> _outputQueue = new(boundedCapacity: 16);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _pumpThread;
    private volatile bool _running = true;
    private volatile bool _drainSent;
    private volatile string? _pumpError;
    private bool _disposed;

    public MediaFoundationH264AsyncEncoder(int width, int height, int bitrateBps, int fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _hnsPerFrame = 10_000_000L / Math.Max(1, fps);
        _nv12Buffer = new byte[width * height * 3 / 2];

        EnsureMfStartup();

        // STEP 1 — Enumerate hardware H.264 encoder MFTs.  Output type filter is
        // H.264 video; no input type filter (let MFTEnumEx return any input
        // format — most HW MFTs support NV12 which is what we feed).
        var outputFilter = H264OutputRegisterTypeInfo();
        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_HW,
            inputType: null,
            outputType: outputFilter,
            out IntPtr pppActivate,
            out uint count);

        if (count == 0 || pppActivate == IntPtr.Zero)
            throw new InvalidOperationException("MFTEnumEx returned 0 hardware H.264 encoder MFTs");

        IMFActivate? chosen = null;
        try
        {
            // Walk the native IMFActivate* array.  Round-2 hand-off report confirmed
            // the shape: pppActivate is a pointer to an array of N IMFActivate native
            // pointers (each 8 bytes on x64).  Wrap each as a SharpGen ComObject via
            // the (IntPtr) ctor pattern.
            for (int i = 0; i < count; i++)
            {
                var nativeActivate = Marshal.ReadIntPtr(pppActivate, i * IntPtr.Size);
                if (nativeActivate == IntPtr.Zero) continue;
                var activate = new IMFActivate(nativeActivate);
                if (chosen == null)
                {
                    chosen = activate;
                    try { MftFriendlyName = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? ""; }
                    catch { MftFriendlyName = "(friendly name unavailable)"; }
                }
                else
                {
                    // Release the unchosen activates.  Round-3 picks the first
                    // sort-ordered entry; future rounds could prefer a specific
                    // vendor (e.g. Intel over discrete NVENC for laptop power).
                    activate.Dispose();
                }
            }

            if (chosen == null)
                throw new InvalidOperationException("MFTEnumEx returned activates but all entries were null");

            // STEP 2 — Activate the chosen MFT and unlock async mode IMMEDIATELY.
            // MS contract: once a HW MFT is activated, EVERY call returns
            // MF_E_TRANSFORM_ASYNC_LOCKED until MF_TRANSFORM_ASYNC_UNLOCK is set
            // on the MFT's attribute store.
            _mft = chosen.ActivateObject<IMFTransform>();
            _mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, (uint)1);
            // LowLatency removes encoder-side GOP buffering — without it the HW
            // encoder may buffer ~30 frames before the first NAL.
            try { _mft.Attributes.Set(SinkWriterAttributeKeys.LowLatency, (uint)1); } catch { /* not all MFTs */ }
        }
        finally
        {
            chosen?.Dispose();
            // Free the IntPtr array itself (CoTaskMemAlloc'd by MF).
            if (pppActivate != IntPtr.Zero) Marshal.FreeCoTaskMem(pppActivate);
        }

        ActiveEncoderDescription = string.IsNullOrEmpty(MftFriendlyName)
            ? "MediaFoundation HW (unnamed MFT)"
            : $"MediaFoundation HW ({MftFriendlyName})";

        // STEP 2.5 — Attach a D3D11 device manager.  Required for AMD/NVENC HW MFTs
        // (Quick Sync sometimes accepts no manager, but AMD MFTs always need one).
        // We create a minimal HW D3D11 device with BGRA + VIDEO support, hand it
        // to a fresh DXGI device manager, and pass that to the MFT via
        // MFT_MESSAGE_SET_D3D_MANAGER BEFORE SetOutputType per MS contract.
        TrySetupD3DManager();

        // STEP 3 — Configure types.  For H.264 encoder MFTs the order is OUTPUT
        // first (the codec/profile/bitrate constraints), then INPUT (the raw
        // format we'll feed).  Same shape as round 2.
        ConfigureOutputType(bitrateBps);
        ConfigureInputType();
        CacheSpsPpsHeader();
        RefreshOutputStreamInfo();

        // STEP 4 — QI for IMFMediaEventGenerator (event pump) and ICodecAPI
        // (runtime control).  Both are technically optional but ICodecAPI being
        // null just means ForceKeyframe/SetMaxBitrate become no-ops (natural
        // GOP keeps things working).  EventGenerator is REQUIRED for async MFTs.
        _eventGen = _mft.QueryInterface<IMFMediaEventGenerator>();
        TryQueryCodecApi();

        // STEP 5 — Begin streaming.  For async MFTs the MS contract is just
        // NotifyBeginStreaming; NotifyStartOfStream is a sync-MFT-only message
        // and sending it to an async MFT can leave it in a stuck state where it
        // raises a phantom HAVE_OUTPUT then refuses to emit any NEED_INPUT
        // (observed on the AMD H.264 MFT during round-3 self-test on the dev
        // box).  Round 2's sync SW MFT does need NotifyStartOfStream — that's
        // why it's still there in MediaFoundationH264Encoder.
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);

        // STEP 6 — Spin up the pump thread.  It blocks on GetEvent and drives
        // the MFT's NeedInput / HaveOutput cadence.
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"MfH264AsyncPump-{_width}x{_height}"
        };
        _pumpThread.Start();
    }

    private void ConfigureOutputType(int bitrateBps)
    {
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType,         MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype,           VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate,        (uint)bitrateBps);
        outType.Set(MediaTypeAttributeKeys.FrameSize,         MediaFactory.PackSize((uint)_width, (uint)_height));
        outType.Set(MediaTypeAttributeKeys.FrameRate,         MediaFactory.PackRatio(_fps, 1));
        outType.Set(MediaTypeAttributeKeys.PixelAspectRatio,  MediaFactory.PackRatio(1, 1));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode,     (uint)VideoInterlaceMode.Progressive);
        // Baseline profile = 66 matches what OpenH264 decoder accepts today.
        // Round 2 self-test (12/12, 3/3 IDRs) is the proof point.
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile,      (uint)66);
        _mft.SetOutputType(0, outType, 0);
    }

    private void ConfigureInputType()
    {
        // MS recommended pattern for HW MFTs: enumerate the MFT's preferred input
        // types via GetInputAvailableType, find the first NV12-subtyped entry,
        // then patch the frame size/rate/aspect/interlace we need and use that
        // as the basis for SetInputType.  Constructing a media type from scratch
        // (round-2 SW MFT pattern) works for the MS SW MFT but not for AMD's HW
        // MFT — AMD requires its own type as the basis or it silently refuses
        // input later with MF_E_NOTACCEPTING.
        IMFMediaType? chosen = null;
        for (int idx = 0; ; idx++)
        {
            IMFMediaType candidate;
            try { candidate = _mft.GetInputAvailableType(0, idx); }
            catch { break; }
            using (candidate)
            {
                try
                {
                    var subtype = candidate.GetGUID(MediaTypeAttributeKeys.Subtype);
                    if (subtype == VideoFormatGuids.NV12)
                    {
                        // Clone-by-copy: create fresh, copy all attrs, then patch.
                        chosen = MediaFactory.MFCreateMediaType();
                        candidate.CopyAllItems(chosen);
                        break;
                    }
                }
                catch { /* try next */ }
            }
        }
        if (chosen == null)
        {
            // Fallback to round-2 shape if MFT didn't advertise any NV12 input.
            chosen = MediaFactory.MFCreateMediaType();
            chosen.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            chosen.Set(MediaTypeAttributeKeys.Subtype,   VideoFormatGuids.NV12);
        }
        using (chosen)
        {
            chosen.Set(MediaTypeAttributeKeys.FrameSize,        MediaFactory.PackSize((uint)_width, (uint)_height));
            chosen.Set(MediaTypeAttributeKeys.FrameRate,        MediaFactory.PackRatio(_fps, 1));
            chosen.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
            chosen.Set(MediaTypeAttributeKeys.InterlaceMode,    (uint)VideoInterlaceMode.Progressive);
            _mft.SetInputType(0, chosen, 0);
        }
    }

    private void CacheSpsPpsHeader()
    {
        try
        {
            using var outType = _mft.GetOutputCurrentType(0);
            var blob = outType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (blob == null || blob.Length == 0) { _spsPpsAnnexB = null; return; }
            var annexB = new byte[4 + blob.Length];
            annexB[0] = 0x00; annexB[1] = 0x00; annexB[2] = 0x00; annexB[3] = 0x01;
            Buffer.BlockCopy(blob, 0, annexB, 4, blob.Length);
            _spsPpsAnnexB = annexB;
        }
        catch { _spsPpsAnnexB = null; }
    }

    private void TrySetupD3DManager()
    {
        IntPtr devicePtr = IntPtr.Zero;
        IntPtr contextPtr = IntPtr.Zero;
        try
        {
            // Create a default-adapter HW D3D11 device with BGRA + Video support.
            // pFeatureLevels=null lets the runtime pick the highest the adapter
            // supports — fine for an encoder-feeding device.
            int hr = D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA | D3D11_CREATE_DEVICE_VIDEO,
                IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out devicePtr, out _, out contextPtr);
            if (hr != 0 || devicePtr == IntPtr.Zero)
                throw new InvalidOperationException($"D3D11CreateDevice failed HRESULT 0x{hr:X8}");

            // Context is unused by the MFT path — release immediately.  Device
            // gets wrapped + retained for the encoder's lifetime.
            if (contextPtr != IntPtr.Zero) { Marshal.Release(contextPtr); contextPtr = IntPtr.Zero; }

            // Enable multi-thread protection on the D3D11 device.  Required by
            // AMD's H.264 MFT (and recommended by MS for any HW MFT) — without
            // it the MFT raises only the InputStreamStateChanged event (type
            // 603) and then refuses all ProcessInput calls with MF_E_NOTACCEPTING.
            EnableD3D11Multithread(devicePtr);

            // Wrap the raw IUnknown* in a ComObject.  SharpGen's ComObject(IntPtr)
            // ctor adopts the pointer without AddRef — we hold the +1 ref from
            // D3D11CreateDevice via this wrapper, and Dispose will Release it.
            _d3dDeviceWrapper = new ComObject(devicePtr);
            devicePtr = IntPtr.Zero;  // ownership transferred to wrapper

            _dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
            _dxgiManager.ResetDevice(_d3dDeviceWrapper).CheckError();

            // Send the manager to the MFT.  param is the manager's IUnknown* cast
            // to UIntPtr — MS contract for SET_D3D_MANAGER.
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager,
                                (UIntPtr)(ulong)_dxgiManager.NativePointer.ToInt64());
            D3DStatus = "ok (D3D11 HW device + DXGI manager set on MFT)";
        }
        catch (Exception ex)
        {
            // D3D setup failure isn't fatal — some HW MFTs (Intel QSV in newer
            // drivers) accept a null D3D manager.  Record the diagnostic so the
            // hand-off report shows the failure, and let the pump's first event
            // tell us if the MFT can run unaccelerated.
            D3DStatus = $"FAIL: {ex.GetType().Name}: {ex.Message}";
            if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);
            if (contextPtr != IntPtr.Zero) Marshal.Release(contextPtr);
            ReleaseD3DResources();
        }
    }

    private void ReleaseD3DResources()
    {
        try { _dxgiManager?.Dispose(); } catch { }
        _dxgiManager = null;
        try { _d3dDeviceWrapper?.Dispose(); } catch { }
        _d3dDeviceWrapper = null;
    }

    private void TryQueryCodecApi()
    {
        // Marshal.QueryInterface from the IMFTransform's underlying COM object.
        // SharpGen's ComObject exposes NativePointer which is the IUnknown* —
        // we can call QueryInterface directly on it via the Win32 imports.
        try
        {
            var iidCodecApi = ICodecAPI.IID;
            int hr = Marshal.QueryInterface(_mft.NativePointer, in iidCodecApi, out IntPtr ppv);
            if (hr == 0 && ppv != IntPtr.Zero)
                _codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(ppv);
            // ppv is AddRef'd by QI — Marshal.GetObjectForIUnknown holds another
            // ref; release the QI ref so refcount stays 1.
            if (ppv != IntPtr.Zero) Marshal.Release(ppv);
        }
        catch { _codecApi = null; }
    }

    // ─────────── IVideoEncoder ───────────

    public EncodedFrame? Encode(Bitmap frame)
    {
        if (_disposed) return null;
        if (frame.Width != _width || frame.Height != _height) return null;
        if (_pumpError != null) return null;  // pump died, stop trying

        // BGRA → NV12 (reuses round 2's identical converter inline — duplicated
        // here to keep the two encoder classes independent so a bug fix to one
        // doesn't risk the other).
        MediaFoundationH264Encoder_Helpers.ConvertBgraToNv12(frame, _nv12Buffer);

        // Build IMFSample around the NV12 bytes with a monotonic timestamp.
        // The pump owns disposing this sample after ProcessInput consumes it.
        IMFSample sample;
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(_nv12Buffer, 0, ptr, _nv12Buffer.Length);
            buffer.Unlock();
            buffer.CurrentLength = _nv12Buffer.Length;
            sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime     = _frameCount * _hnsPerFrame;
            sample.SampleDuration = _hnsPerFrame;
            buffer.Dispose();  // sample holds its own ref
            _frameCount++;
        }

        // Push to pump; if the queue is full (capture got way ahead of encode),
        // drop the oldest input — same lossy semantics as the broadcaster's
        // outbound channel for a live screen-share.
        if (!_inputQueue.TryAdd(sample, 50 /* ms */))
        {
            sample.Dispose();
        }

        // Poll the output queue without blocking.  The first few Encode calls
        // typically return null (HW MFT is filling its pipeline); subsequent
        // calls return the previous frame's encoded NAL.  ~1 frame of latency.
        if (_outputQueue.TryTake(out var ef, 0))
            return ef;
        return null;
    }

    /// <summary>Self-test escape hatch — issues an MFT drain on the first call
    /// and pulls flushed frames out of the output queue, then returns null when
    /// drained.  Production callers don't need this; broadcasters' per-frame
    /// Encode handles the live stream.</summary>
    public EncodedFrame? Drain()
    {
        if (_disposed) return null;
        if (!_drainSent)
        {
            try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); }
            catch { /* MFT may already be draining */ }
            _drainSent = true;
        }
        // Wait briefly — the pump needs time to receive HAVE_OUTPUT events
        // from the drain.  300ms is plenty for any reasonable HW encoder.
        if (_outputQueue.TryTake(out var ef, 300))
            return ef;
        return null;
    }

    /// <summary>Phase 11-B inc2 part 2 round 3 — runtime IDR-on-demand via
    /// ICodecAPI.SetValue(CODECAPI_AVEncVideoForceKeyFrame, true).  Returns
    /// true if the call succeeded; false if ICodecAPI isn't bound on this
    /// MFT (some HW MFTs don't expose it — caller treats false as "next IDR
    /// happens on the natural GOP boundary").</summary>
    public bool ForceKeyframe()
    {
        if (_disposed || _codecApi == null) return false;
        try
        {
            var key = ICodecAPI.CODECAPI_AVEncVideoForceKeyFrame;
            object value = (uint)1;
            int hr = _codecApi.SetValue(in key, ref value);
            return hr == 0;
        }
        catch { return false; }
    }

    /// <summary>Phase 11-B inc2 part 2 round 3 — runtime bitrate change via
    /// ICodecAPI.SetValue(CODECAPI_AVEncCommonMeanBitRate, bps).  Silent no-op
    /// if ICodecAPI isn't bound; the broadcaster's display-mirror fields still
    /// update.</summary>
    public void SetMaxBitrate(int bitsPerSecond)
    {
        if (_disposed || _codecApi == null) return;
        try
        {
            var key = ICodecAPI.CODECAPI_AVEncCommonMeanBitRate;
            object value = (uint)bitsPerSecond;
            _codecApi.SetValue(in key, ref value);
        }
        catch { /* swallow — natural rate-control continues */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _cts.Cancel();

        // Wake the pump if it's blocked on GetEvent — queue a benign event so
        // it returns and checks _running.  QueueEvent of Error(1) is the
        // simplest poison pill that the pump's default case will ignore.
        try { _eventGen?.QueueEvent((int)MediaEventTypes.Error, Guid.Empty, default, null); }
        catch { /* if the gen is already gone, the pump will exit on cts */ }

        try { _pumpThread?.Join(2000); } catch { }

        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }

        // Drain queues to release the COM samples we held.
        while (_inputQueue.TryTake(out var s)) { try { s.Dispose(); } catch { } }
        _inputQueue.Dispose();
        _outputQueue.Dispose();
        _cts.Dispose();

        if (_codecApi != null) { try { Marshal.ReleaseComObject(_codecApi); } catch { } _codecApi = null; }
        try { _eventGen?.Dispose(); } catch { }
        try { _mft?.Dispose(); } catch { }
        _mft = null!;
        _eventGen = null!;
        ReleaseD3DResources();
    }

    // ─────────── Pump thread ───────────

    /// <summary>
    /// Hybrid pump — drains events non-blocking via GetEvent(NO_WAIT), then
    /// pro-actively calls ProcessInput when input is queued (independent of
    /// whether the MFT raised METransformNeedInput).
    ///
    /// Why hybrid: the standard MS async-MFT pattern is "wait for NEED_INPUT,
    /// then ProcessInput; wait for HAVE_OUTPUT, then ProcessOutput".  Round 3's
    /// dev-box AMD H.264 MFT does NOT raise NEED_INPUT after streaming begins
    /// — only an InputStreamStateChanged event (type 603) fires.  Pro-active
    /// ProcessInput works because MFT_E_NOTACCEPTING is returned and tolerated
    /// when the MFT is genuinely full.  This pattern also works fine on MFTs
    /// that DO raise NEED_INPUT (we just feed faster than they ask).
    /// </summary>
    private void PumpLoop()
    {
        try
        {
            while (_running)
            {
                bool didWork = TryDrainOneEvent() | TryFeedOneInput();
                if (!didWork)
                {
                    // No event ready and no input queued — yield briefly.  2ms
                    // keeps latency tight without burning CPU on an idle pump.
                    if (_cts.Token.WaitHandle.WaitOne(2)) break;
                }
            }
        }
        catch (Exception ex)
        {
            _pumpError = $"pump: {ex.Message}";
        }
    }

    private bool TryDrainOneEvent()
    {
        IMFMediaEvent? ev = null;
        try { ev = _eventGen.GetEvent(MF_EVENT_FLAG_NO_WAIT); }
        catch (SharpGenException) { return false; }   // MF_E_NO_EVENTS_AVAILABLE or shutdown
        if (ev == null) return false;

        using (ev)
        {
            int type = (int)ev.EventType;
            if (_firstEventTypes.Count < 8)
                lock (_firstEventTypes) _firstEventTypes.Add(type);

            if (type == ME_TRANSFORM_NEED_INPUT)
            {
                // Counted but not acted on — the proactive feeder pushes input.
                Interlocked.Increment(ref _evNeedInput);
            }
            else if (type == ME_TRANSFORM_HAVE_OUTPUT)
            {
                Interlocked.Increment(ref _evHaveOutput);
                try
                {
                    var frame = DrainOneOutput();
                    if (frame.HasValue && !_outputQueue.IsAddingCompleted)
                        _outputQueue.TryAdd(frame.Value, 50);
                }
                catch (SharpGenException sge) when ((uint)sge.HResult == 0x8000FFFFu)
                {
                    // Tolerated phantom HAVE_OUTPUT (seen on AMD H.264 MFT before
                    // streaming truly engages).  Not an error — continue pumping.
                }
                catch (Exception ex) { _pumpError = $"HaveOutput: {ex.Message}"; }
            }
            else if (type == ME_TRANSFORM_DRAIN_COMPLETE)
            {
                Interlocked.Increment(ref _evDrainComplete);
                _drainSent = true;
            }
            else
            {
                Interlocked.Increment(ref _evOther);
            }
        }
        return true;
    }

    private bool TryFeedOneInput()
    {
        if (!_inputQueue.TryTake(out var sample, 0)) return false;
        try
        {
            _mft.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException sge) when (sge.HResult == MF_E_NOTACCEPTING)
        {
            // MFT is full — re-enqueue this sample so the next pump tick
            // tries again after some output drains.  If the queue is full,
            // drop (lossy semantics for live screen-share).
            if (!_inputQueue.TryAdd(sample, 0)) sample.Dispose();
            Interlocked.Increment(ref _notAcceptingHits);
            return true;
        }
        catch (Exception ex)
        {
            _pumpError = $"ProcessInput: {ex.Message}";
            sample.Dispose();
            return true;
        }
        sample.Dispose();
        return true;
    }

    // HRESULT 0xC00D6D61 — MF_E_TRANSFORM_STREAM_CHANGE.  Async H.264 encoder
    // MFTs (especially AMD) raise a phantom HAVE_OUTPUT event right after
    // NotifyBeginStreaming asking us to renegotiate the output type before
    // streaming starts.  ProcessOutput returns this; we re-fetch the available
    // output type, re-SetOutputType, refresh the SPS/PPS cache, and the next
    // HAVE_OUTPUT event arrives with real data.
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    // Output stream info is cached at ctor + after any stream change.  AMD's
    // MFT can return spurious Size=0 mid-stream if asked while it's juggling
    // a format renegotiation.
    private OutputStreamInfo _cachedOutputInfo;
    private bool _mftProvidesSample;

    private void RefreshOutputStreamInfo()
    {
        _cachedOutputInfo = _mft.GetOutputStreamInfo(0);
        _mftProvidesSample = (_cachedOutputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        LastOutputStreamFlags = $"Flags=0x{_cachedOutputInfo.Flags:X} Size={_cachedOutputInfo.Size} Alignment={_cachedOutputInfo.Alignment} providesSample={_mftProvidesSample}";
    }

    /// <summary>Handles MF_E_TRANSFORM_STREAM_CHANGE — re-negotiate the output
    /// media type using whichever the MFT now reports as available, then
    /// refresh the SPS/PPS cache so future IDRs still get the prepend.</summary>
    private void HandleStreamChange()
    {
        try
        {
            using var newOut = _mft.GetOutputAvailableType(0, 0);
            _mft.SetOutputType(0, newOut, 0);
            CacheSpsPpsHeader();
            RefreshOutputStreamInfo();
        }
        catch (Exception ex)
        {
            _pumpError = $"stream-change: {ex.Message}";
        }
    }

    /// <summary>Pulls one output sample from the MFT after a HAVE_OUTPUT event.
    /// Reuses round-2's logic for MFT-provides-samples vs caller-allocates and
    /// for the IDR SPS/PPS prepend.  Handles MF_E_TRANSFORM_STREAM_CHANGE by
    /// renegotiating output type and signalling caller to skip this event.</summary>
    private EncodedFrame? DrainOneOutput()
    {
        IMFSample? outSample = null;
        IMFMediaBuffer? outBuffer = null;
        if (!_mftProvidesSample)
        {
            outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_cachedOutputInfo.Size, 4096));
            outSample = MediaFactory.MFCreateSample();
            outSample.AddBuffer(outBuffer);
        }

        var outputs = new OutputDataBuffer { StreamID = 0, Sample = outSample! };

        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref outputs, out _);

        if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            outSample?.Dispose();
            outBuffer?.Dispose();
            HandleStreamChange();
            return null;  // skip this event; next HAVE_OUTPUT will have real data
        }
        if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            outSample?.Dispose();
            outBuffer?.Dispose();
            return null;
        }
        hr.CheckError();

        if (_mftProvidesSample)
        {
            outSample = outputs.Sample;
            outBuffer = outSample.GetBufferByIndex(0);
        }
        if (outBuffer == null || outSample == null)
            throw new InvalidOperationException("ProcessOutput returned no buffer/sample");

        outBuffer.Lock(out IntPtr ptr, out _, out int currentLen);
        var data = new byte[currentLen];
        Marshal.Copy(ptr, data, 0, currentLen);
        outBuffer.Unlock();

        bool isKeyframe = false;
        try { isKeyframe = outSample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; }
        catch { }

        if (isKeyframe && _spsPpsAnnexB != null)
        {
            var combined = new byte[_spsPpsAnnexB.Length + data.Length];
            Buffer.BlockCopy(_spsPpsAnnexB, 0, combined, 0, _spsPpsAnnexB.Length);
            Buffer.BlockCopy(data, 0, combined, _spsPpsAnnexB.Length, data.Length);
            data = combined;
        }

        outBuffer?.Dispose();
        outSample?.Dispose();

        return new EncodedFrame { Data = data, IsKeyframe = isKeyframe };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICodecAPI — Vortice 3.6.2 does NOT bind this interface (verified via probe
// round 3, section "Look for ICodecAPI in Vortice").  Declare a minimal
// [ComImport] surface here for ForceKeyframe + SetMaxBitrate.  IID + property
// GUIDs come straight from codecapi.h.
// ─────────────────────────────────────────────────────────────────────────────

[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    // Only the methods we use; the others are stubbed out so the COM vtable
    // layout matches MSDN's declaration order.
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, IntPtr valueMin, IntPtr valueMax, IntPtr valueDelta);
    [PreserveSig] int GetParameterValues(in Guid api, IntPtr values, IntPtr valuesCount);
    [PreserveSig] int GetDefaultValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    [PreserveSig] int RegisterForEvent(in Guid api, long userData);
    [PreserveSig] int UnregisterForEvent(in Guid api);
    [PreserveSig] int SetAllDefaults();
    [PreserveSig] int SetValueWithNotify(in Guid api, IntPtr value, out IntPtr changedParam, out int changedParamCount);
    [PreserveSig] int SetAllDefaultsWithNotify(IntPtr value, out IntPtr changedParam, out int changedParamCount);
    [PreserveSig] int GetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParam, out int changedParamCount);

    public static readonly Guid IID = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D2630B5");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate  = new("f7222374-2144-4815-b550-a37f8e283bb0");
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal helpers — exposed only so the async encoder can reuse round-2's
// BGRA→NV12 converter without forcing the SW encoder class to expose internals.
// Concrete impl is in MediaFoundationH264Encoder.cs (round 2); this small
// trampoline keeps both classes independent at the file level while sharing
// the one piece of correctness-sensitive code that should not diverge.
// ─────────────────────────────────────────────────────────────────────────────
internal static class MediaFoundationH264Encoder_Helpers
{
    /// <summary>BGRA→NV12 BT.601 limited-range integer conversion.  Pre-allocated
    /// <paramref name="nv12"/> must be width*height*3/2 bytes.</summary>
    internal static void ConvertBgraToNv12(Bitmap bgra, byte[] nv12)
    {
        int w = bgra.Width, h = bgra.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bgra.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int yPlaneSize = w * h;
            unsafe
            {
                byte* src = (byte*)data.Scan0;
                fixed (byte* dst = nv12)
                {
                    int yIdx = 0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = src + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte b = row[x * 4 + 0];
                            byte g = row[x * 4 + 1];
                            byte r = row[x * 4 + 2];
                            int Y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                            dst[yIdx++] = (byte)Math.Clamp(Y, 0, 255);
                        }
                    }
                    int uvIdx = yPlaneSize;
                    for (int y = 0; y < h; y += 2)
                    {
                        byte* row = src + y * stride;
                        for (int x = 0; x < w; x += 2)
                        {
                            byte b = row[x * 4 + 0];
                            byte g = row[x * 4 + 1];
                            byte r = row[x * 4 + 2];
                            int U = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                            int V = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                            dst[uvIdx++] = (byte)Math.Clamp(U, 0, 255);
                            dst[uvIdx++] = (byte)Math.Clamp(V, 0, 255);
                        }
                    }
                }
            }
        }
        finally { bgra.UnlockBits(data); }
    }
}
