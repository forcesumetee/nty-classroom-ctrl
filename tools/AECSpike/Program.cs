// Phase 13-D step 0 — WASAPI AEC feasibility spike.
//
// Goal: give the dev the information needed to decide whether Tier 3 voice
// chat can rely on Windows' built-in AEC (Layer 2 of the 3-layer strategy in
// docs/breakout-rooms-tier3-design.md §5) on this hardware.
//
// What this tool can verify programmatically:
//   1. The default Communications-role capture device exists and can be
//      opened via WasapiCapture (the API Tier 3's StudentMicBroadcaster would
//      use).  Opening a Communications-role endpoint engages Windows' built-in
//      AEC + noise suppression on supported drivers.
//   2. The negotiated capture format matches what Tier 3 expects (16 kHz mono
//      16-bit) — or how far off it is so we know whether a resample stage is
//      needed in the broadcaster.
//   3. Per-100-ms RMS over a 5-second capture window — useful as a feedback
//      gauge for the dev's subjective test below.
//
// What the dev does subjectively (this is the actual AEC verdict):
//   - Run twice: once with the speakers silent ("baseline"), once with a loud
//     loopback playing through the speakers ("loopback").  Speak roughly the
//     same volume into the mic both runs.
//   - Compare the per-window RMS traces.  If AEC is engaged, the loopback run
//     RMS should be CLOSE to the baseline run (speaker audio cancelled out of
//     mic).  If AEC is bypassed, the loopback run will be much louder.
//   - Subjective ear test: with PTT-style hold-to-talk simulated by the dev,
//     does the local speaker bleed into the mic and feed back when played
//     back?  (Tier 3 PTT mode prevents the feedback by serializing speakers.)
//
// Exit code: always 0 if the tool ran (does NOT return failure on "AEC
// disabled" — the verdict is the dev's call after running the subjective test).
// Non-zero only on tool-level errors (no devices, runtime crash).

using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Globalization;

const int RunSeconds = 5;
const int WindowMs = 100;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Phase 13-D step 0 — WASAPI AEC spike ===");
Console.WriteLine();

// ──────── Step A — enumerate endpoints ────────
var enumr = new MMDeviceEnumerator();

void DumpDevice(string label, MMDevice? d)
{
    if (d == null) { Console.WriteLine($"  {label,-44} (none)"); return; }
    Console.WriteLine($"  {label,-44} {d.FriendlyName}");
    Console.WriteLine($"  {"",44} state={d.State} id={d.ID}");
}

Console.WriteLine("Default endpoints by role (the Communications role is what Tier 3 will use):");
MMDevice? defCommCap = null;
MMDevice? defConsCap = null;
MMDevice? defCommRender = null;
MMDevice? defConsRender = null;
try { defCommCap   = enumr.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { }
try { defConsCap   = enumr.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }
try { defCommRender = enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications); } catch { }
try { defConsRender = enumr.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); } catch { }
DumpDevice("Capture / Communications", defCommCap);
DumpDevice("Capture / Console", defConsCap);
DumpDevice("Render  / Communications", defCommRender);
DumpDevice("Render  / Console", defConsRender);

if (defCommCap != null && defConsCap != null && defCommCap.ID == defConsCap.ID)
    Console.WriteLine("  → Communications + Console capture point at the SAME device. Common on laptops.");
else
    Console.WriteLine("  → Communications and Console capture differ — Tier 3 will target the Communications endpoint.");
Console.WriteLine();

Console.WriteLine("All active capture endpoints:");
foreach (var d in enumr.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
{
    Console.WriteLine($"  · {d.FriendlyName}  (id={d.ID})");
}
Console.WriteLine();

if (defCommCap == null)
{
    Console.Error.WriteLine("FAIL: no default Communications capture endpoint available. Tier 3 has no mic to capture from.");
    return 2;
}

// ──────── Step B — open via WasapiCapture (Communications role = AEC engaged) ────────
//
// NAudio's WasapiCapture(MMDevice) constructor opens the endpoint in shared
// mode with the device's mix format.  When the endpoint is the Communications
// role and the driver supports it, Windows applies AEC + noise suppression
// inside the audio engine before frames reach us.  Verifying that "AEC is
// engaged" via API is non-trivial (no flag returned) — the subjective RMS
// comparison is the actual evidence.
Console.WriteLine($"Opening Communications capture endpoint via WasapiCapture …");
WasapiCapture? cap = null;
try
{
    cap = new WasapiCapture(defCommCap);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: WasapiCapture(...) threw: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
var fmt = cap.WaveFormat;
Console.WriteLine($"  Negotiated capture format: {fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample}-bit, encoding={fmt.Encoding}");
if (fmt.SampleRate != 16000 || fmt.Channels != 1 || fmt.BitsPerSample != 16)
{
    Console.WriteLine("  → Tier 3 target is 16 kHz mono 16-bit; broadcaster will need a resample/convert stage.");
}
else
{
    Console.WriteLine("  → Native format matches Tier 3 target. No resample needed.");
}
Console.WriteLine();

// ──────── Step C — record N seconds, report per-window RMS ────────
Console.WriteLine($"Capturing {RunSeconds} s of mic audio. Per-{WindowMs}ms RMS will be printed.");
Console.WriteLine("  Run instructions for the dev subjective test:");
Console.WriteLine("    BASELINE run: speakers silent, speak normally into mic.  Note the RMS levels.");
Console.WriteLine("    LOOPBACK run: play loud audio (any classroom audio) through speakers,");
Console.WriteLine("                  speak the same way into mic.  RMS should be SIMILAR to baseline");
Console.WriteLine("                  if WASAPI AEC is cancelling the loopback.  If RMS is much higher,");
Console.WriteLine("                  AEC is not engaged on this hardware.");
Console.WriteLine();
Console.WriteLine("  Press Enter to start the run …");
Console.ReadLine();

var samplesPerWindow = fmt.SampleRate * WindowMs / 1000;
var bytesPerSample = fmt.BitsPerSample / 8 * fmt.Channels;
var bytesPerWindow = samplesPerWindow * bytesPerSample;
var rmsValues = new List<double>(RunSeconds * 1000 / WindowMs + 4);
var carry = new byte[0];

cap.DataAvailable += (_, e) =>
{
    // Coalesce captured bytes into WindowMs-sized blocks and compute RMS.
    var combined = new byte[carry.Length + e.BytesRecorded];
    Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
    Buffer.BlockCopy(e.Buffer, 0, combined, carry.Length, e.BytesRecorded);
    int offset = 0;
    while (combined.Length - offset >= bytesPerWindow)
    {
        var rms = ComputeRms(combined, offset, bytesPerWindow, fmt.BitsPerSample, fmt.Encoding);
        rmsValues.Add(rms);
        offset += bytesPerWindow;
    }
    if (offset < combined.Length)
    {
        carry = new byte[combined.Length - offset];
        Buffer.BlockCopy(combined, offset, carry, 0, carry.Length);
    }
    else
    {
        carry = Array.Empty<byte>();
    }
};

cap.StartRecording();
Thread.Sleep(TimeSpan.FromSeconds(RunSeconds));
cap.StopRecording();
Thread.Sleep(200);   // drain
cap.Dispose();

Console.WriteLine();
Console.WriteLine($"RMS trace ({rmsValues.Count} windows × {WindowMs} ms):");
for (int i = 0; i < rmsValues.Count; i++)
{
    var bar = Bar(rmsValues[i]);
    Console.WriteLine($"  [{i * WindowMs,5} ms]  {rmsValues[i],8:F1} dBFS  {bar}");
}
double mean = rmsValues.Average();
double max = rmsValues.Max();
Console.WriteLine();
Console.WriteLine($"Summary:  mean={mean:F1} dBFS  peak={max:F1} dBFS  windows={rmsValues.Count}");
Console.WriteLine();
Console.WriteLine("Verdict guidance (for dev to fill in):");
Console.WriteLine("  · If LOOPBACK-run mean RMS is within ~3-6 dBFS of BASELINE-run → AEC is working.");
Console.WriteLine("  · If LOOPBACK-run mean RMS is 15-30 dBFS HIGHER than baseline → AEC is bypassed or absent.");
Console.WriteLine("  · Intermediate → partial AEC; default still acceptable, document USB headset.");
Console.WriteLine();
return 0;


// ──────── Helpers ────────

static double ComputeRms(byte[] data, int offset, int len, int bitsPerSample, WaveFormatEncoding encoding)
{
    // 16-bit PCM is the common case.  Float (IEEE) and 32-bit int are also
    // possible when the device negotiates a high-quality mix format.
    double sumSquares = 0;
    int n = 0;
    if (bitsPerSample == 16 && encoding == WaveFormatEncoding.Pcm)
    {
        for (int i = offset; i < offset + len; i += 2)
        {
            short s = (short)(data[i] | (data[i + 1] << 8));
            double v = s / 32768.0;
            sumSquares += v * v;
            n++;
        }
    }
    else if (bitsPerSample == 32 && encoding == WaveFormatEncoding.IeeeFloat)
    {
        for (int i = offset; i < offset + len; i += 4)
        {
            float v = BitConverter.ToSingle(data, i);
            sumSquares += v * v;
            n++;
        }
    }
    else if (bitsPerSample == 32 && encoding == WaveFormatEncoding.Pcm)
    {
        for (int i = offset; i < offset + len; i += 4)
        {
            int s = BitConverter.ToInt32(data, i);
            double v = s / 2147483648.0;
            sumSquares += v * v;
            n++;
        }
    }
    else
    {
        return double.NaN;
    }
    if (n == 0) return double.NegativeInfinity;
    double rms = Math.Sqrt(sumSquares / n);
    if (rms < 1e-9) return -120.0;
    return 20.0 * Math.Log10(rms);
}

static string Bar(double db)
{
    // Map -90..0 dBFS → 0..40 char bar.
    if (double.IsNaN(db)) return "(format unsupported)";
    double clamped = Math.Max(-90.0, Math.Min(0.0, db));
    int width = (int)((clamped + 90.0) / 90.0 * 40.0);
    return new string('█', width);
}
