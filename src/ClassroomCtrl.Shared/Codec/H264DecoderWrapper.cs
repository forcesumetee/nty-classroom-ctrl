using H264Sharp;
using System;
using System.IO;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 4 Part 4: Thin wrapper around H264Sharp decoder for screen viewing.
/// Accepts H.264 NAL bytes, outputs RGB pixel data (caller decides pixel format mapping for WPF).
///
/// Phase 10.15.1 — first BUG-001 attempt.  Flipped <c>noDelay=false</c> to <c>true</c> per
/// the H264Sharp XML doc recommendation ("This is a Cisco feature and its reccomended to be
/// set to true").  Necessary, not sufficient — every Decode still returned false on real
/// hardware.
///
/// Phase 10.15.2 — actual BUG-001 fix.  The library docs on the RgbImage Decode overload
/// say "Decodes encoded data into the requested RGB color space defined in ImageFormat of
/// RgbImage."  Our wrapper passed <c>RgbImage? img = null</c>, so the decoder had no format
/// hint and no destination buffer — and silently returned false.  The README in the
/// upstream H264Sharp repo (commit b9a97bf) shows the correct pattern: pre-allocate the
/// <c>RgbImage</c> with explicit <c>ImageFormat</c> + <c>width</c> + <c>height</c>, and
/// reuse the same instance across decode calls (decoder fills the buffer rather than
/// reassigning the ref).  We pre-allocate <c>Bgra32</c> at <c>1920×1080</c> to match
/// both <c>StudentBroadcaster</c> and <c>ScreenBroadcaster</c>'s fixed target dimensions
/// and the format both <c>StudentScreenWindow.MapFormat</c> and the per-student recording
/// tee already expect.
///
/// TODO: Migrate to Firefox-style runtime download from ciscobinary.openh264.org
///       if shipping volume exceeds the MPEG-LA AVC pool 100K units/year free tier.
/// </summary>
public sealed class H264DecoderWrapper : IDisposable
{
    private readonly H264Decoder _decoder = new();
    private bool _disposed;

    /// <summary>Phase 10.15.2 — pre-allocated RGB destination buffer for Decode.  See class
    /// doc for the BUG-001 story.  Reused across every TryDecode call for the wrapper's
    /// lifetime; the decoder writes pixels into this buffer rather than reassigning the
    /// ref (matches the upstream README example pattern).  Disposed in <see cref="Dispose"/>.
    /// </summary>
    private RgbImage _rgbImage;

    /// <summary>Phase 10.15.2 — dimensions the buffer was sized for.  Logged once if the
    /// decoder ever reports a different actual frame size, which would indicate either a
    /// new broadcaster resolution or buffer-overflow risk.</summary>
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;

    /// <summary>Phase 10.15.1 — count of failed decodes; we log the DecodingState
    /// returned by OpenH264 for the first few drops so a future BUG-001-style regression
    /// surfaces its actual error code instead of a silent <c>return false</c>.</summary>
    private int _failedDecodes;

    private bool _dimensionMismatchLogged;

    /// <summary>
    /// Default ctor — pre-allocates a <c>Bgra32 1920×1080</c> buffer that matches the
    /// fixed target dimensions both broadcasters in this codebase encode at.  Callers
    /// that need a different size can use the (width, height) overload.
    /// </summary>
    public H264DecoderWrapper() : this(1920, 1080) { }

    public H264DecoderWrapper(int width, int height)
    {
        _bufferWidth = width;
        _bufferHeight = height;

        var rc = _decoder.Initialize();
        if (rc != 0)
        {
            _decoder.Dispose();
            throw new InvalidOperationException($"OpenH264 decoder Initialize failed (code {rc})");
        }

        // Phase 10.15.2 — pre-allocate Bgra32 at the broadcaster's fixed target size.
        // The Decode RgbImage overload reads .Format off this buffer to decide which
        // color space to convert YUV into; passing null (the pre-10.15.2 behavior)
        // silently fails because the decoder has no format to convert to.
        _rgbImage = new RgbImage(ImageFormat.Bgra, width, height);
    }

    /// <summary>
    /// Decode one H.264 NAL bundle. Returns true on success with rgb data + dimensions + format.
    /// May return false legitimately when the decoder needs more data (e.g. waiting for keyframe);
    /// callers should drop the frame and continue.
    /// </summary>
    public bool TryDecode(byte[] nal, out byte[] rgb, out int width, out int height, out ImageFormat format)
    {
        rgb = Array.Empty<byte>();
        width = 0;
        height = 0;
        format = ImageFormat.Bgr;

        if (_disposed || nal == null || nal.Length == 0) return false;

        try
        {
            DecodingState state;
            // Phase 10.15.2 — pass the pre-allocated _rgbImage; the decoder writes into
            // its buffer and updates Width/Height/Format reflecting the actual frame.
            // Phase 10.15.1 — noDelay=true per H264Sharp recommendation.
            bool ok = _decoder.Decode(nal, 0, nal.Length, true, out state, ref _rgbImage);
            if (!ok)
            {
                LogDecodeFailure(state, nal.Length);
                return false;
            }

            width = _rgbImage.Width;
            height = _rgbImage.Height;
            format = _rgbImage.Format;

            // Phase 10.15.2 — defensive: log once if the decoded frame dimensions don't
            // match what we pre-allocated for.  Our broadcasters fix 1920×1080 so this
            // shouldn't fire, but if a future broadcaster change forgets to update the
            // wrapper too, the log will catch the overflow risk.
            if (!_dimensionMismatchLogged && (width != _bufferWidth || height != _bufferHeight))
            {
                _dimensionMismatchLogged = true;
                TryAppendLog($"Decoded frame {width}x{height} differs from pre-allocated buffer {_bufferWidth}x{_bufferHeight}");
            }

            rgb = _rgbImage.GetBytes();
            for (int i = 0; i + 2 < rgb.Length; i += 4)
            {
                byte tmp = rgb[i];
                rgb[i] = rgb[i + 2];
                rgb[i + 2] = tmp;
            }
            return width > 0 && height > 0 && rgb.Length > 0;
        }
        catch (Exception ex)
        {
            LogDecodeException(ex, nal.Length);
            return false;
        }
    }

    /// <summary>
    /// Phase 10.15.1 — record the first few decode-failure states to a file so future
    /// regressions don't require source instrumentation.  Throttled to first 5 failures.
    /// Path: <c>%TEMP%\h264decoder-debug.log</c>.  Shared by Teacher and Student processes;
    /// the timestamp + state value is enough to identify the calling side.
    /// </summary>
    private void LogDecodeFailure(DecodingState state, int nalLength)
    {
        if (_failedDecodes >= 5) { _failedDecodes++; return; }
        _failedDecodes++;
        TryAppendLog($"Decode failed: state={state} ({(int)state}) nalLength={nalLength}");
    }

    private void LogDecodeException(Exception ex, int nalLength)
    {
        if (_failedDecodes >= 5) { _failedDecodes++; return; }
        _failedDecodes++;
        TryAppendLog($"Decode threw: {ex.GetType().Name}: {ex.Message} nalLength={nalLength}");
    }

    private static readonly object _logLock = new();
    private static void TryAppendLog(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "h264decoder-debug.log");
            lock (_logLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] [H264DecoderWrapper] {msg}\n");
            }
        }
        catch { /* never let a log write crash the render path */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Phase 10.15.2 — release the pre-allocated native RGB buffer too (~8 MB at 1920×1080 Bgra32).
        try { _rgbImage?.Dispose(); } catch { }
        try { _decoder.Dispose(); } catch { }
    }
}
