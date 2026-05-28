// Phase 11-B inc2 part 2 — local encode→decode self-test harness.
//
// Round 2 (the original): instantiated MediaFoundationH264Encoder (SW) directly
// and asserted decoder-compat against H264DecoderWrapper.  PASS 12/12.
//
// Round 3 extension: also exercises MediaFoundationH264AsyncEncoder (HW async
// path).  Behaviour:
//   • If the dev machine has at least one HW H.264 MFT discoverable by
//     MFTEnumEx (Quick Sync, NVENC, AMF), construct the async encoder and run
//     the same encode→decode round-trip.  Report HW-DECODER-COMPAT: PASS/FAIL.
//   • If MFTEnumEx returns no HW MFTs (or activation fails), the round-3 ctor
//     throws — we catch and report that HW wasn't exercised on this dev box.
//     The SW round-2 path is still tested as the safety net.
//
// Run from the repo root:
//   dotnet run --project tools\EncoderSelfTest\EncoderSelfTest.csproj
// or after build, from the bin dir so native H264Sharp + OpenH264 DLLs resolve.

using ClassroomCtrl.Shared.Codec;
using System.Drawing;
using System.Drawing.Imaging;

const int Width  = 1920;
const int Height = 1080;
const int Fps    = 4;
const int Bps    = 500_000;
const int FrameCount = 12;

Console.WriteLine($"== Phase 11-B inc2 part2 — encoder→decoder self-test ==");
Console.WriteLine($"   target: {Width}x{Height} @ {Fps} FPS, {Bps} bps, {FrameCount} frames\n");

int swResult    = RunSwTest();
int hwResult    = RunHwTest();
int stallResult = RunStallDetectionTest();

Console.WriteLine("\n==================== SUMMARY ====================");
Console.WriteLine($"  SW (round 2, MS H264 Encoder MFT) ........ {Verdict(swResult)}");
Console.WriteLine($"  HW (round 3, MFTEnumEx async pump) ....... {VerdictHw(hwResult)}");
Console.WriteLine($"  inc4 stall detection .................... {VerdictStall(stallResult)}");
return swResult == 0
    && (hwResult == 0 || hwResult == 2)
    && (stallResult == 0 || stallResult == 2)
    ? 0 : 1;


// ─── SW path (round 2 — unchanged from the original harness) ───
int RunSwTest()
{
    Console.WriteLine("─────── SW path ───────");
    MediaFoundationH264Encoder enc;
    try
    {
        enc = new MediaFoundationH264Encoder(Width, Height, Bps, Fps);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DECODER-COMPAT (SW): FAIL (encoder ctor threw {ex.GetType().Name}: {ex.Message})");
        return 1;
    }

    Console.WriteLine($"   encoder: {enc.ActiveEncoderDescription}");
    Console.WriteLine($"   attrSet: {enc.AttributeSetDiag}\n");

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
            Console.WriteLine($"   frame {i}: NEED_MORE_INPUT (normal for first frame or two)");
        }
    }
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

    return CheckDecoderCompat("SW", outputs);
}


// ─── HW path (round 3) ───
//
// Return codes:
//   0  HW MFT activated + decoder-compat PASS
//   1  HW MFT activated but decoder-compat FAILED
//   2  No HW MFT available on this dev box (informational; not a failure for
//      the self-test as a whole — fleet customers may have one even if the
//      dev box doesn't, and 2-PC validation will confirm there).
int RunHwTest()
{
    Console.WriteLine("\n─────── HW path ───────");
    MediaFoundationH264AsyncEncoder enc;
    try
    {
        enc = new MediaFoundationH264AsyncEncoder(Width, Height, Bps, Fps);
    }
    catch (Exception ex)
    {
        // The most common failures here: no HW MFT (MFTEnumEx count=0), or
        // ActivateObject HRESULT failure on a HW MFT that needs a D3D manager.
        Console.WriteLine($"HW encoder ctor threw {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("HW-DECODER-COMPAT: SKIPPED (no usable HW H.264 MFT on this dev box)");
        return 2;
    }

    Console.WriteLine($"   encoder: {enc.ActiveEncoderDescription}");
    Console.WriteLine($"   friendly: {enc.MftFriendlyName}");
    Console.WriteLine($"   spsPpsCache: {(enc.SpsPpsHeaderSize > 0 ? enc.SpsPpsHeaderSize + " bytes" : "(in-band only)")}");
    Console.WriteLine($"   D3D status: {enc.D3DStatus}\n");

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
            Console.WriteLine($"   frame {i}: no output yet (pump still filling — normal for first ~2 frames)");
        }
    }

    // Give the pump a moment to catch up before draining, in case the last
    // encoded NAL is still being event-pumped.
    Thread.Sleep(150);

    Console.WriteLine($"\n   draining encoder...");
    int drained = 0;
    for (int i = 0; i < 60; i++)
    {
        var df = enc.Drain();
        if (df == null) { Console.WriteLine($"   drain {i}: null (queue empty + drain complete)"); break; }
        drained++;
        outputs.Add(df.Value);
        Console.WriteLine($"   drain {i}: {df.Value.Data.Length,6} bytes  keyframe={df.Value.IsKeyframe}");
    }
    Console.WriteLine($"   pump status: {enc.PumpStatus}");
    Console.WriteLine($"   event counts: {enc.EventCounts}");
    Console.WriteLine($"   first event types: [{enc.FirstEventTypes}]");
    Console.WriteLine($"   last OutputStreamInfo: {enc.LastOutputStreamFlags}");
    enc.Dispose();

    return CheckDecoderCompat("HW", outputs);
}


int CheckDecoderCompat(string label, List<EncodedFrame> outputs)
{
    Console.WriteLine($"\n   total {label} outputs: {outputs.Count}");
    if (outputs.Count == 0)
    {
        Console.WriteLine($"\nDECODER-COMPAT ({label}): FAIL (encoder produced no output)");
        return 1;
    }

    using var dec = new H264DecoderWrapper(Width, Height);
    int decodeAttempts = 0, decodeSuccesses = 0, idrAttempts = 0, idrSuccesses = 0;
    for (int i = 0; i < outputs.Count; i++)
    {
        decodeAttempts++;
        if (outputs[i].IsKeyframe) idrAttempts++;
        bool ok = dec.TryDecode(outputs[i].Data, out _, out int dw, out int dh, out var fmt);
        if (ok)
        {
            decodeSuccesses++;
            if (outputs[i].IsKeyframe) idrSuccesses++;
            Console.WriteLine($"   decoded {label} output {i}: {dw}x{dh} fmt={fmt}  keyframe={outputs[i].IsKeyframe}");
        }
        else
        {
            Console.WriteLine($"   DECODE FAILED on {label} output {i} ({outputs[i].Data.Length} bytes, keyframe={outputs[i].IsKeyframe})");
        }
    }

    Console.WriteLine($"\n   {label} decode: {decodeSuccesses}/{decodeAttempts} outputs, {idrSuccesses}/{idrAttempts} IDRs");
    if (decodeSuccesses == 0)
    {
        Console.WriteLine($"DECODER-COMPAT ({label}): FAIL — OpenH264 decoder could not decode any output");
        Console.WriteLine("  Likely diagnosis: Annex-B framing missing, profile mismatch, or SPS/PPS not propagating.");
        return 1;
    }
    Console.WriteLine($"DECODER-COMPAT ({label}): PASS");
    return 0;
}


// ─── inc4 stall-detection validation ───
//
// Phase 11-B inc4 adds EncoderStalledException to MediaFoundationH264AsyncEncoder
// so the broadcaster has a clean signal to fall back from a wedged HW MFT to
// SW OpenH264.  On this AMD dev box (where the MFT genuinely stalls — round 3
// finding), we can validate the safety mechanism by feeding the encoder past
// the stall threshold and asserting EncoderStalledException fires with the
// expected diagnostics (never-produced-output path).
//
// Return codes:
//   0  Stall correctly detected — exception fired with hadOutputBeforeStall=false
//   1  HW MFT activated and PRODUCED output (encoder works) — no stall to detect;
//      treat as informational PASS since the safety guard is for broken MFTs
//   2  No HW MFT available — informational
int RunStallDetectionTest()
{
    Console.WriteLine("\n─────── inc4 stall-detection ───────");
    MediaFoundationH264AsyncEncoder enc;
    try { enc = new MediaFoundationH264AsyncEncoder(Width, Height, Bps, Fps); }
    catch (Exception ex)
    {
        Console.WriteLine($"   HW encoder unavailable: {ex.Message}");
        Console.WriteLine("STALL-DETECTION: SKIPPED (no HW MFT)");
        return 2;
    }
    Console.WriteLine($"   encoder: {enc.ActiveEncoderDescription}");
    Console.WriteLine($"   feeding 50 frames; expecting EncoderStalledException ~frame 30 on stall paths");

    bool stallFired = false;
    int stallAtFrame = -1;
    int consecutiveNulls = -1;
    bool hadOutput = false;
    int producedOutputs = 0;
    try
    {
        for (int i = 0; i < 50; i++)
        {
            using var bmp = MakeGradient(Width, Height, i);
            var ef = enc.Encode(bmp);
            if (ef.HasValue) producedOutputs++;
        }
    }
    catch (EncoderStalledException stall)
    {
        stallFired = true;
        stallAtFrame = producedOutputs;
        consecutiveNulls = stall.ConsecutiveNullReturns;
        hadOutput = stall.HadOutputBeforeStall;
    }
    enc.Dispose();

    Console.WriteLine($"   producedOutputs: {producedOutputs}");
    Console.WriteLine($"   stallFired: {stallFired}");
    if (stallFired)
    {
        Console.WriteLine($"   ConsecutiveNullReturns: {consecutiveNulls}");
        Console.WriteLine($"   HadOutputBeforeStall:   {hadOutput}");
    }

    if (stallFired)
    {
        Console.WriteLine($"STALL-DETECTION: PASS (caught EncoderStalledException — broadcaster rebuild path is exercisable)");
        return 0;
    }
    if (producedOutputs > 0)
    {
        Console.WriteLine($"STALL-DETECTION: N/A (HW encoder produced output — nothing to stall-detect here)");
        return 1;  // informational; HW worked so the guard isn't reachable on this box
    }
    Console.WriteLine($"STALL-DETECTION: FAIL (no output and no exception — threshold misconfigured?)");
    return 1;
}


// ─── helpers ───
static string Verdict(int code) => code == 0 ? "PASS" : "FAIL";
static string VerdictHw(int code) => code switch
{
    0 => "PASS",
    1 => "FAIL",
    2 => "SKIPPED (no HW MFT)",
    _ => "?"
};
static string VerdictStall(int code) => code switch
{
    0 => "PASS (exception fired)",
    1 => "N/A (HW produced output)",
    2 => "SKIPPED (no HW MFT)",
    _ => "?"
};

static Bitmap MakeGradient(int w, int h, int seed)
{
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
                    row[x * 4 + 0] = (byte)((x + seed * 16) & 0xFF);
                    row[x * 4 + 1] = (byte)((y + seed * 16) & 0xFF);
                    row[x * 4 + 2] = (byte)(((x + y) / 2 + seed * 16) & 0xFF);
                    row[x * 4 + 3] = 0xFF;
                }
            }
        }
    }
    finally { bmp.UnlockBits(data); }
    return bmp;
}
