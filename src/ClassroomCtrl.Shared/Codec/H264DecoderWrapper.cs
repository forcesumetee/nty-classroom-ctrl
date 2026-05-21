using H264Sharp;
using System;
using System.IO;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 4 Part 4: Thin wrapper around H264Sharp decoder for screen viewing.
/// Accepts H.264 NAL bytes, outputs RGB pixel data (caller decides pixel format mapping for WPF).
///
/// Phase 10.15.1 — BUG-001 fix.  The previous implementation passed <c>noDelay=false</c>
/// to <c>H264Decoder.Decode</c>, which the H264Sharp library docs explicitly call out as
/// the non-recommended setting (the XML doc on the Decode method says noDelay
/// "is a Cisco feature and its reccomended to be set to true").  With <c>false</c>,
/// OpenH264 buffers output waiting for future reference frames that never arrive in our
/// per-window viewer flow, so every TryDecode call returned false — including the keyframe
/// that contained SPS+PPS+IDR.  Confirmed via Phase 10.15 diagnostic logging at four
/// pipeline boundaries: encoder produced valid Annex B (NAL header 0x67 = SPS), the bytes
/// arrived at Teacher byte-for-byte intact, the event fired with match=true, the decoder
/// was created successfully, but every Decode returned false.  Switching to <c>true</c>
/// is consistent with the live-streaming use case (Baseline profile, no B-frames).
///
/// TODO: Migrate to Firefox-style runtime download from ciscobinary.openh264.org
///       if shipping volume exceeds the MPEG-LA AVC pool 100K units/year free tier.
/// </summary>
public sealed class H264DecoderWrapper : IDisposable
{
    private readonly H264Decoder _decoder = new();
    private bool _disposed;

    /// <summary>Phase 10.15.1 — count of failed decodes; we log the DecodingState
    /// returned by OpenH264 for the first few drops so a future BUG-001-style regression
    /// surfaces its actual error code instead of a silent <c>return false</c>.</summary>
    private int _failedDecodes;

    public H264DecoderWrapper()
    {
        var rc = _decoder.Initialize();
        if (rc != 0)
        {
            _decoder.Dispose();
            throw new InvalidOperationException($"OpenH264 decoder Initialize failed (code {rc})");
        }
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
            // RgbImage overload of Decode uses `ref` (not `out`) — must pre-declare.
            // The decoder reassigns/allocates the image; we just hand it a starting reference.
            RgbImage? img = null;
            DecodingState state;
            // Phase 10.15.1 — noDelay=true per H264Sharp library recommendation.  See class
            // doc above for the full BUG-001 root-cause story.
            bool ok = _decoder.Decode(nal, 0, nal.Length, true, out state, ref img!);
            if (!ok || img == null)
            {
                LogDecodeFailure(state, nal.Length);
                return false;
            }

            width = img.Width;
            height = img.Height;
            format = img.Format;
            rgb = img.GetBytes();
            img.Dispose();
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
        try { _decoder.Dispose(); } catch { }
    }
}
