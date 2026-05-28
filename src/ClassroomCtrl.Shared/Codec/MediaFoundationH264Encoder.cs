using ClassroomCtrl.Shared.Protocol;
using SharpGen.Runtime;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 2 (round 2) — Media Foundation H.264 encoder, software path.
///
/// Round 1 of part 2 wrote ~400 lines against guessed Vortice 3.6.2 API names and
/// the build failed on 8 identifiers.  Round 2 resolved every name against the
/// restored Vortice.MediaFoundation.dll via reflection (see hand-off report's
/// "Vortice 3.6.2 API surface — resolved punchlist" table) BEFORE writing any
/// encode code.  This file is the result.
///
/// Path: Microsoft software H.264 encoder MFT (CLSID 6CA50344-051A-4DED-9779-A43305165E35),
/// instantiated directly via CoCreateInstance.  Synchronous mode — sample in,
/// sample out.  Output: Annex-B byte stream, Baseline profile, CBR at the target
/// bitrate, with SPS/PPS prepended on every IDR so the existing OpenH264 decoder
/// (post-10.15.2) gets a fully self-contained stream.
///
/// What this round delivers (the spec's stated crux):
///   • The MF→OpenH264 decoder-compat gate is testable locally via the encode→
///     decode self-test harness at <c>tools/EncoderSelfTest</c>.
///   • Factory selects this when <c>UseHardwareH264</c> is on and codec is H264.
///   • Active-encoder log distinguishes "MediaFoundation SW (MS H264 Encoder MFT)"
///     from "OpenH264 SW".
///
/// What's still deferred to round 3 (async HW MFT for real Quick Sync / NVENC / AMF):
///   • MFTEnumEx + async MFT pump thread + MFT_TRANSFORM_ASYNC_UNLOCK + event
///     generator.  Round 2 deliberately uses direct CoCreateInstance of the
///     known SW MFT CLSID to skip both the IntPtr-array marshalling in Vortice's
///     MFTEnumEx and the async machinery.  Round 3 swaps both in.
///   • ICodecAPI runtime ForceKeyframe / SetMaxBitrate.  Stays no-op this round;
///     natural IDR period (~2 s via MFT's default GOP) keeps late-joiners
///     decodable and the broadcaster's display-mirror fields keep log lines
///     accurate.
///
/// COM/MF lifetime: <c>MFStartup</c> + <c>CoInitializeEx</c> are idempotent
/// per-process via <see cref="Interlocked.CompareExchange"/>; <c>MFShutdown</c>
/// is intentionally NOT called (letting the OS clean up at process exit avoids
/// tear-down races with other MF consumers like the Recording service's NReco
/// path).  Per-frame samples and buffers are scoped with <c>using</c> so they
/// release on the encode-call stack; the MFT itself releases in
/// <see cref="Dispose"/>.
/// </summary>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    public VideoCodec Codec => VideoCodec.H264;

    public string ActiveEncoderDescription { get; private set; } = "(uninitialised)";

    /// <summary>Round 2 self-test diagnostic — exposes the MFT's <c>MF_TRANSFORM_ASYNC</c>
    /// attribute so the self-test can confirm whether sync-mode handling is even
    /// applicable.  Async MFTs (which includes the MS SW H.264 MFT on Windows 8+)
    /// don't produce output via plain ProcessOutput after ProcessInput; they need
    /// an event-driven pump.  See round 2 hand-off report.</summary>
    public bool IsAsyncMft { get; private set; }
    private static readonly Guid MF_TRANSFORM_ASYNC        = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e83d54b9-4d7e-487c-aafe-d7afe1e72cba");
    private static readonly Guid MF_LOW_LATENCY            = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    /// <summary>Round 2 diagnostic — last exception message from setting MFT attributes.</summary>
    public string AttributeSetDiag { get; private set; } = "";

    /// <summary>Round 2 self-test diagnostic — bytes of the cached Annex-B SPS/PPS
    /// header (0 if CacheSpsPpsHeader couldn't read MpegSequenceHeader from the MFT).</summary>
    public int SpsPpsHeaderSize => _spsPpsAnnexB?.Length ?? 0;

    // ─────────── CO/MF init plumbing ───────────

    private static int _mfStartedFlag;
    private static void EnsureMfStartup()
    {
        if (Interlocked.CompareExchange(ref _mfStartedFlag, 1, 0) != 0) return;
        // The capture-loop thread is a Task worker which has no apartment by default.
        // CoCreateInstance needs one; MTA is fine for MFTs.  CoInitializeEx returns
        // S_OK on first call, S_FALSE if already initialised — both acceptable.
        const uint COINIT_MULTITHREADED = 0x0;
        _ = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        MediaFactory.MFStartup(useLightVersion: false).CheckError();
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        in Guid riid, out IntPtr ppv);

    // CLSID + IID for the Microsoft software H.264 encoder MFT, taken straight from
    // mftransform.h / codecapi.h.  Round 3 (async HW path) will MFTEnumEx for the
    // hardware MFTs Quick Sync / NVENC / AMF — same IID, different CLSIDs.
    private static readonly Guid CLSID_MSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid IID_IMFTransform       = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private const uint CLSCTX_INPROC_SERVER = 1;

    // HRESULT 0xC00D6D72 — MF_E_TRANSFORM_NEED_MORE_INPUT — the MFT consumed all
    // available input and the next ProcessOutput should be deferred until more
    // input is pushed.  Maps to "null this tick" in our IVideoEncoder contract.
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    // ─────────── Encoder state ───────────

    private IMFTransform _mft = null!;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _hnsPerFrame;
    private readonly byte[] _nv12Buffer;
    private byte[]? _spsPpsAnnexB;
    private long _frameCount;
    private bool _disposed;

    public MediaFoundationH264Encoder(int width, int height, int bitrateBps, int fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _hnsPerFrame = 10_000_000L / Math.Max(1, fps);
        _nv12Buffer = new byte[width * height * 3 / 2];

        EnsureMfStartup();

        // CoCreate the MS SW H.264 encoder MFT directly.  Round 3 swaps this for
        // MFTEnumEx-driven HW MFT selection.
        var hr = CoCreateInstance(in CLSID_MSH264EncoderMFT, IntPtr.Zero,
            CLSCTX_INPROC_SERVER, in IID_IMFTransform, out IntPtr ppv);
        if (hr != 0 || ppv == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CoCreateInstance(CLSID_MSH264EncoderMFT) failed with HRESULT 0x{hr:X8}");

        _mft = new IMFTransform(ppv);
        ActiveEncoderDescription = "MS H264 Encoder MFT";

        // Diagnostic: is this MFT async?  The MS SW H.264 encoder MFT is flagged
        // async on Windows 8+, which makes the sync ProcessInput→ProcessOutput
        // pump produce zero output (output is delivered via METransformHaveOutput
        // events on IMFMediaEventGenerator instead).
        try { IsAsyncMft = _mft.Attributes.GetUInt32(MF_TRANSFORM_ASYNC) != 0; }
        catch { IsAsyncMft = false; }

        // Unconditionally try to set both MF_TRANSFORM_ASYNC_UNLOCK and MF_LOW_LATENCY.
        // Round 2 found the MFT's MF_TRANSFORM_ASYNC query returns "not set" (catch
        // hides the real state), so the IsAsyncMft check is unreliable.  Setting the
        // unlock attribute on a sync MFT is harmless; setting LOW_LATENCY removes
        // the encoder's GOP buffering so a fresh stream emits its first NAL within
        // a frame or two instead of buffering ~30 frames.  Both must be set BEFORE
        // SetOutputType per MSDN.
        var diag = new System.Text.StringBuilder();
        try { _mft.Attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, (uint)1); diag.Append("async_unlock=ok; "); }
        catch (Exception ex) { diag.Append($"async_unlock={ex.GetType().Name}; "); }
        try { _mft.Attributes.Set(MF_LOW_LATENCY, (uint)1); diag.Append("low_latency=ok; "); }
        catch (Exception ex) { diag.Append($"low_latency={ex.GetType().Name}; "); }
        AttributeSetDiag = diag.ToString();

        ConfigureOutputType(bitrateBps);
        ConfigureInputType();
        CacheSpsPpsHeader();

        // Some MFTs require these before the first ProcessInput.  Pass UIntPtr.Zero
        // for the "param" arg per the MFT_MESSAGE_NOTIFY_* contract.
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void ConfigureOutputType(int bitrateBps)
    {
        // Output type MUST be set before input type for H.264 encoder MFTs.
        // IMFMediaType inherits IMFAttributes — typed Set overloads land
        // attributes onto the media type directly.
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType,         MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype,           VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate,        (uint)bitrateBps);
        // Pack32As64: width in the high 32, height in the low 32 — verified shape
        // via MediaFactory.PackSize.
        outType.Set(MediaTypeAttributeKeys.FrameSize,         MediaFactory.PackSize((uint)_width, (uint)_height));
        outType.Set(MediaTypeAttributeKeys.FrameRate,         MediaFactory.PackRatio(_fps, 1));
        outType.Set(MediaTypeAttributeKeys.PixelAspectRatio,  MediaFactory.PackRatio(1, 1));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode,     (uint)VideoInterlaceMode.Progressive);
        // H.264 Baseline profile = 66 per the standard.  Matches what the existing
        // OpenH264 decoder consumes today; no constraint flag needed for OpenH264 to
        // decode.  Round 3 may switch to Constrained Baseline (256 in MF's enum
        // mapping) if the HW MFTs require it.
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile,      (uint)66);

        _mft.SetOutputType(0, outType, 0);
    }

    private void ConfigureInputType()
    {
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType,        MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype,          VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.FrameSize,        MediaFactory.PackSize((uint)_width, (uint)_height));
        inType.Set(MediaTypeAttributeKeys.FrameRate,        MediaFactory.PackRatio(_fps, 1));
        inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
        inType.Set(MediaTypeAttributeKeys.InterlaceMode,    (uint)VideoInterlaceMode.Progressive);

        _mft.SetInputType(0, inType, 0);
    }

    /// <summary>
    /// After SetOutputType, the MFT exposes the H.264 sequence header (SPS + PPS)
    /// via <c>MediaTypeAttributeKeys.MpegSequenceHeader</c> on the current output
    /// media type.  The blob is the raw sequence — we frame it as Annex-B with a
    /// single leading start code and prepend it on every IDR output so the
    /// existing OpenH264 decoder sees a fully self-contained stream regardless of
    /// whether the MFT itself emits SPS/PPS in-band.
    /// </summary>
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

    public EncodedFrame? Encode(Bitmap frame)
    {
        if (_disposed) return null;
        if (frame.Width != _width || frame.Height != _height) return null;

        // BGRA → NV12 into pre-allocated buffer (no GC churn per frame).
        ConvertBgraToNv12(frame, _nv12Buffer);

        // Build IMFSample around the NV12 buffer with a monotonic timestamp.
        using var inBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
        inBuffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(_nv12Buffer, 0, ptr, _nv12Buffer.Length);
        inBuffer.Unlock();
        inBuffer.CurrentLength = _nv12Buffer.Length;

        using var inSample = MediaFactory.MFCreateSample();
        inSample.AddBuffer(inBuffer);
        inSample.SampleTime     = _frameCount * _hnsPerFrame;
        inSample.SampleDuration = _hnsPerFrame;

        _mft.ProcessInput(0, inSample, 0);
        _frameCount++;

        return TryDrainOutput();
    }

    /// <summary>Diagnostic — last GetOutputStreamInfo result for the self-test.</summary>
    public string LastOutputStreamInfo { get; private set; } = "";

    /// <summary>
    /// Round 2 self-test escape hatch — after all input has been pushed, the
    /// MFT may still be holding buffered frames behind its GOP latency.  Drain
    /// flushes them out as a sequence of EncodedFrame outputs.  Call repeatedly
    /// until it returns null.  Production callers (the broadcasters) don't need
    /// this; their per-frame Encode call already drains one frame at a time and
    /// dropping the tail at Stop is acceptable for a live screen-share stream.
    /// </summary>
    public EncodedFrame? Drain()
    {
        if (_disposed) return null;
        return TryDrainOutput();
    }

    private EncodedFrame? TryDrainOutput()
    {
        // GetOutputStreamInfo reports the MFT's required output buffer size and
        // whether the MFT provides its own samples.  For the MS SW H.264 MFT the
        // caller provides the sample.
        var info = _mft.GetOutputStreamInfo(0);
        LastOutputStreamInfo = $"Flags=0x{info.Flags:X} Size={info.Size} Alignment={info.Alignment}";

        // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100.  When set, the MFT itself
        // allocates the IMFSample and we must NOT preallocate one.  Pre-round-2
        // assumption was that the MS H.264 SW MFT doesn't provide samples; if
        // that's wrong, the empty sample blocks the encoder.  Branch on the flag.
        const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
        bool mftProvidesSample = (info.Flags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        IMFSample? outSample = null;
        IMFMediaBuffer? outBuffer = null;
        if (!mftProvidesSample)
        {
            outBuffer = MediaFactory.MFCreateMemoryBuffer(info.Size);
            outSample = MediaFactory.MFCreateSample();
            outSample.AddBuffer(outBuffer);
        }

        var outputs = new OutputDataBuffer { StreamID = 0, Sample = outSample! };

        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref outputs, out _);
        if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            outSample?.Dispose();
            outBuffer?.Dispose();
            return null;
        }
        hr.CheckError();

        // If MFT provided the sample, retrieve it from the struct after the call.
        if (mftProvidesSample)
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

        // CleanPoint nonzero = keyframe (IDR for H.264).  Not all MFTs set it;
        // default false on a missing attribute, in which case the SPS/PPS prepend
        // simply doesn't fire — natural in-band emission carries the IDR's params.
        bool isKeyframe = false;
        try { isKeyframe = outSample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; }
        catch { /* attribute absent — leave default false */ }

        if (isKeyframe && _spsPpsAnnexB != null)
        {
            var combined = new byte[_spsPpsAnnexB.Length + data.Length];
            Buffer.BlockCopy(_spsPpsAnnexB, 0, combined, 0, _spsPpsAnnexB.Length);
            Buffer.BlockCopy(data, 0, combined, _spsPpsAnnexB.Length, data.Length);
            data = combined;
        }

        // Release the sample/buffer regardless of who provided them.
        outBuffer?.Dispose();
        outSample?.Dispose();

        return new EncodedFrame { Data = data, IsKeyframe = isKeyframe };
    }

    /// <summary>
    /// Phase 11-B inc2 part 2 round 2 — runtime IDR-on-demand requires
    /// ICodecAPI.SetValue(CODECAPI_AVEncVideoForceKeyFrame, true) via
    /// Marshal.QueryInterface.  Deferred to a later sub-follow-up; the MFT's
    /// natural intra period (~2 s) keeps late-joiners decodable.  Returns false
    /// matching MJpegEncoder's "n/a" contract.
    /// </summary>
    public bool ForceKeyframe() => false;

    /// <summary>
    /// Phase 11-B inc2 part 2 round 2 — runtime bitrate change requires
    /// ICodecAPI.SetValue(CODECAPI_AVEncCommonMeanBitRate, bps).  Deferred to
    /// a later sub-follow-up; the encoder retains its initial bitrate.  The
    /// broadcaster's display-mirror fields still update on AdaptiveBitrate
    /// events so log lines stay accurate, but the on-wire bitrate is frozen
    /// at construction time until ICodecAPI lands.
    /// </summary>
    public void SetMaxBitrate(int bitsPerSecond) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            try { _mft?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
            // Drain any buffered output.  At most a couple of frames in sync mode.
            for (int i = 0; i < 4; i++)
            {
                try { if (TryDrainOutput() == null) break; } catch { break; }
            }
            try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        }
        finally
        {
            try { _mft?.Dispose(); } catch { }
            _mft = null!;
        }
    }

    // ─────────── BGRA → NV12 (BT.601 limited-range, integer math) ───────────

    /// <summary>
    /// CPU BGRA→NV12 conversion using BT.601 limited-range coefficients (same
    /// fixed-point integer math as FFmpeg's BGRA→NV12 swscale path).
    /// Pre-allocated <paramref name="nv12"/> must be width*height*3/2 bytes.
    /// Y plane is width*height bytes laid out row-major; UV plane follows,
    /// width*height/2 bytes laid out as U,V,U,V… at half resolution (NV12).
    /// </summary>
    private static void ConvertBgraToNv12(Bitmap bgra, byte[] nv12)
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
                    // Y plane — every pixel.
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
                    // UV plane — half resolution, interleaved U,V per 2×2 block.
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
