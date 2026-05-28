// Phase 11-B inc2 part 2 round 2 — local decoder-compat self-test harness.
//
// Round 2's stated gate: the Media Foundation SW H.264 encoder's Annex-B output
// must decode on the existing OpenH264-based H264DecoderWrapper (the same one
// used by every viewer in production post-10.15.2).  This harness runs that
// gate without needing a 2-PC setup: encode N synthetic frames in-process, feed
// the Annex-B output into H264DecoderWrapper.TryDecode, and assert the decoder
// reports success at least once on the first IDR.
//
// Run from the repo root:
//   dotnet run --project tools\EncoderSelfTest\EncoderSelfTest.csproj
// or, after build, from the bin dir so the native H264Sharp + OpenH264 DLLs
// resolve from the same folder.

using ClassroomCtrl.Shared.Codec;
using System.Drawing;
using System.Drawing.Imaging;

const int Width  = 1920;
const int Height = 1080;
const int Fps    = 4;
const int Bps    = 500_000;
const int FrameCount = 12;

Console.WriteLine($"== Phase 11-B inc2 part2 round 2 — encoder→decoder self-test ==");
Console.WriteLine($"   target: {Width}x{Height} @ {Fps} FPS, {Bps} bps, {FrameCount} frames\n");

MediaFoundationH264Encoder enc;
try
{
    enc = new MediaFoundationH264Encoder(Width, Height, Bps, Fps);
}
catch (Exception ex)
{
    Console.WriteLine($"DECODER-COMPAT: FAIL (encoder ctor threw {ex.GetType().Name}: {ex.Message})");
    return 1;
}

Console.WriteLine($"   encoder: {enc.ActiveEncoderDescription}");
Console.WriteLine($"   isAsync: {(enc.IsAsyncMft ? "YES (MFT is async — sync pump won't produce output)" : "no")}");
Console.WriteLine($"   attrSet: {enc.AttributeSetDiag}\n");

// Encode FrameCount synthetic moving-gradient frames.  Each frame is a Bitmap of
// 32bpp ARGB pixels — the same shape ScreenBroadcaster / StudentBroadcaster
// pass to IVideoEncoder.Encode.  Distinct content per frame keeps the encoder
// honest (no run-length collapse).
var outputs = new List<EncodedFrame>();
for (int i = 0; i < FrameCount; i++)
{
    using var bmp = MakeGradient(Width, Height, i);
    var ef = enc.Encode(bmp);
    if (ef.HasValue)
    {
        outputs.Add(ef.Value);
        Console.WriteLine($"   frame {i}: encoded {ef.Value.Data.Length,6} bytes  keyframe={ef.Value.IsKeyframe}");
    }
    else
    {
        Console.WriteLine($"   frame {i}: encoder returned null (NEED_MORE_INPUT — normal for the first frame or two)");
    }
}
Console.WriteLine($"\n   GetOutputStreamInfo: {enc.LastOutputStreamInfo}");
Console.WriteLine($"   _spsPpsAnnexB cached: {(enc.SpsPpsHeaderSize > 0 ? enc.SpsPpsHeaderSize + " bytes" : "NO (CacheSpsPpsHeader returned empty)")}");

// Round 2 self-test — drain any frames the encoder is still buffering.
Console.WriteLine($"\n   draining encoder...");
int drained = 0;
for (int i = 0; i < 30; i++)
{
    var df = enc.Drain();
    if (df == null) { Console.WriteLine($"   drain {i}: null (MFT empty)"); break; }
    drained++;
    outputs.Add(df.Value);
    Console.WriteLine($"   drain {i}: {df.Value.Data.Length,6} bytes  keyframe={df.Value.IsKeyframe}");
}

enc.Dispose();

Console.WriteLine($"\n   total outputs: {outputs.Count} ({outputs.Count - drained} during Encode + {drained} during Drain) / {FrameCount} input frames");

if (outputs.Count == 0)
{
    Console.WriteLine("\nDECODER-COMPAT: FAIL (encoder produced no output at all)");
    return 1;
}

// Feed outputs into the existing H264DecoderWrapper.
using var dec = new H264DecoderWrapper(Width, Height);
int decodeAttempts = 0;
int decodeSuccesses = 0;
int idrAttempts = 0;
int idrSuccesses = 0;
for (int i = 0; i < outputs.Count; i++)
{
    decodeAttempts++;
    if (outputs[i].IsKeyframe) idrAttempts++;
    bool ok = dec.TryDecode(outputs[i].Data, out _, out int dw, out int dh, out var fmt);
    if (ok)
    {
        decodeSuccesses++;
        if (outputs[i].IsKeyframe) idrSuccesses++;
        Console.WriteLine($"   decoded output {i}: {dw}x{dh} fmt={fmt}  keyframe={outputs[i].IsKeyframe}");
    }
    else
    {
        Console.WriteLine($"   DECODE FAILED on output {i} ({outputs[i].Data.Length} bytes, keyframe={outputs[i].IsKeyframe})");
    }
}

Console.WriteLine($"\n   decode attempts: {decodeAttempts}");
Console.WriteLine($"   decode successes: {decodeSuccesses}");
Console.WriteLine($"   IDR attempts: {idrAttempts}");
Console.WriteLine($"   IDR successes: {idrSuccesses}");

if (decodeSuccesses == 0)
{
    Console.WriteLine("\nDECODER-COMPAT: FAIL — OpenH264 decoder could not decode any MF encoder output.");
    Console.WriteLine("  Likely diagnosis: Annex-B framing missing, profile mismatch, or SPS/PPS not propagating.");
    Console.WriteLine("  Check %TEMP%\\h264decoder-debug.log for state codes.");
    return 1;
}

Console.WriteLine($"\nDECODER-COMPAT: PASS ({decodeSuccesses}/{decodeAttempts} outputs decoded; " +
                  $"{idrSuccesses}/{idrAttempts} IDRs decoded)");
return 0;

// ─── helpers ───

static Bitmap MakeGradient(int w, int h, int seed)
{
    // Moving diagonal gradient — distinct frame-to-frame so the encoder can't
    // short-circuit a static input.  Output is 32bpp ARGB matching what the
    // broadcasters' GDI capture produces.
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
        unsafe
        {
            byte* dst = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int y = 0; y < h; y++)
            {
                byte* row = dst + y * stride;
                for (int x = 0; x < w; x++)
                {
                    row[x * 4 + 0] = (byte)((x + seed * 16) & 0xFF);            // B
                    row[x * 4 + 1] = (byte)((y + seed * 16) & 0xFF);            // G
                    row[x * 4 + 2] = (byte)(((x + y) / 2 + seed * 16) & 0xFF);  // R
                    row[x * 4 + 3] = 0xFF;                                       // A
                }
            }
        }
    }
    finally { bmp.UnlockBits(data); }
    return bmp;
}
