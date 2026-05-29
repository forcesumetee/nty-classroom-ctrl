// Phase 14-A step 0 — webcam capture feasibility spike.
//
// Goal: give the dev the information needed to commit to a capture library +
// frame pipeline for Tier 1 of Feature #4 (Conference Mode) before any
// feature code lands.  The 13-A precedent (AEC spike before Tier 3 voice) is
// the template — same hard-gate pattern.
//
// What this tool verifies programmatically:
//   1. Webcam device(s) enumerable via DirectShow (the library already used by
//      Teacher's Phase 9.5 CameraBroadcastService).  If zero devices, Tier 1
//      cam-toggle UI must default-disable; spike output establishes "no-cam"
//      handling is not a corner case but THE case on some classroom PCs.
//   2. Default device opens via AForge.Video.DirectShow.VideoCaptureDevice
//      (same API path Tier 2's student-side CameraBroadcaster would use).
//   3. Negotiated capture format (resolution × FPS).  Reveals whether the
//      broadcaster can stay at the target 640×480 @ 10-15 FPS or has to
//      negotiate down.
//   4. Capture 5 seconds; log per-frame arrival time + raw bitmap size.
//      Computes actual FPS, frame-interval jitter (stddev), and average raw
//      frame size in bytes.
//   5. Encodes a few sample frames as JPEG at Q70 (Phase 9.5's setting) and
//      reports encoded size — feeds the bandwidth math in
//      docs/conference-mode-architecture.md §6.
//   6. Records the topology summary so the dev can paste into the README's
//      "Dev box results" section without re-running.
//
// What this tool does NOT verify:
//   - Multi-monitor + window-position interaction (irrelevant for cam capture).
//   - Concurrent screen-share + cam capture CPU impact (Tier 1 spec will note
//     this and Tier 1 implementation should validate it on 2-PC test).
//   - HW encoder integration (deferred to Tier 3 if bandwidth math demands).
//
// Exit code: 0 if tool ran AND at least one frame arrived; non-zero only on
// tool-level errors (no devices, device-open failure, zero-frame capture).
// The format/FPS/jitter VERDICT is the dev's call after reading the trace,
// same as the AEC spike's subjective verdict.

using AForge.Video;
using AForge.Video.DirectShow;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

const int RunSeconds = 5;
const int TargetWidth = 640;
const int TargetHeight = 480;
const int SampleEncodeEvery = 10;   // encode every Nth captured frame to JPEG for size sampling
const long JpegQuality = 70L;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Phase 14-A step 0 — webcam capture feasibility spike ===");
Console.WriteLine();

// ──────── Step A — enumerate webcam devices ────────
Console.WriteLine("Enumerating video input devices (DirectShow / FilterCategory.VideoInputDevice):");
FilterInfoCollection devices;
try
{
    devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: FilterInfoCollection ctor threw: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine("  → DirectShow filter graph isn't available on this Windows install.  Tier 1 must surface a clear error to the teacher.");
    return 2;
}

if (devices.Count == 0)
{
    Console.WriteLine("  (none)");
    Console.WriteLine();
    Console.Error.WriteLine("FAIL: no video input devices.  This is a SUPPORTED state — Tier 1 cam-toggle UI must default-disable with tooltip 'No webcam detected'.");
    Console.Error.WriteLine("      But for spike purposes, we can't measure capture behavior.  Plug in a webcam and rerun.");
    return 3;
}

for (int i = 0; i < devices.Count; i++)
{
    Console.WriteLine($"  [{i}] {devices[i].Name}");
    Console.WriteLine($"      moniker: {devices[i].MonikerString}");
}
Console.WriteLine();
Console.WriteLine($"Will use device [0] = {devices[0].Name}.");
Console.WriteLine();

// ──────── Step B — open + dump capabilities ────────
VideoCaptureDevice device;
try
{
    device = new VideoCaptureDevice(devices[0].MonikerString);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: VideoCaptureDevice ctor threw: {ex.GetType().Name}: {ex.Message}");
    return 4;
}

Console.WriteLine("Reported VideoCapabilities (each = a {resolution, FPS, format} the driver advertises):");
var caps = device.VideoCapabilities ?? Array.Empty<VideoCapabilities>();
if (caps.Length == 0)
{
    Console.WriteLine("  (driver advertised zero capabilities — AForge will fall back to driver default)");
}
else
{
    foreach (var c in caps.OrderBy(c => c.FrameSize.Width * c.FrameSize.Height))
    {
        Console.WriteLine($"  · {c.FrameSize.Width,5}×{c.FrameSize.Height,-5}  avg={c.AverageFrameRate,3} max={c.MaximumFrameRate,3} FPS  bpp={c.BitCount}");
    }
}
Console.WriteLine();

// Pick capability closest to (TargetWidth × TargetHeight).
var chosen = caps
    .OrderBy(c => Math.Abs(c.FrameSize.Width - TargetWidth) + Math.Abs(c.FrameSize.Height - TargetHeight))
    .FirstOrDefault();
if (chosen != null)
{
    device.VideoResolution = chosen;
    Console.WriteLine($"Selected capability: {chosen.FrameSize.Width}×{chosen.FrameSize.Height} @ avg={chosen.AverageFrameRate} FPS.  Tier 1 target was {TargetWidth}×{TargetHeight}.");
}
else
{
    Console.WriteLine($"No reported capabilities; letting driver pick default.  Tier 1 target was {TargetWidth}×{TargetHeight}.");
}
Console.WriteLine();

// ──────── Step C — capture N seconds, log per-frame timing ────────
var frameTimes = new List<long>();
var frameSizes = new List<long>();
var encodedJpegSizes = new List<int>();
int frameCount = 0;
int firstWidth = 0, firstHeight = 0;
PixelFormat firstPixelFormat = PixelFormat.Undefined;
var sw = Stopwatch.StartNew();

device.NewFrame += (object sender, NewFrameEventArgs e) =>
{
    long tNow = sw.ElapsedMilliseconds;
    var bmp = e.Frame;
    if (frameCount == 0)
    {
        firstWidth = bmp.Width;
        firstHeight = bmp.Height;
        firstPixelFormat = bmp.PixelFormat;
    }
    // Approximate raw bitmap size (stride × height); BitmapData would be more
    // accurate but adds locking overhead per frame and we're sampling.
    int bytesPerPixel = Image.GetPixelFormatSize(bmp.PixelFormat) / 8;
    long rawSize = (long)bmp.Width * bmp.Height * bytesPerPixel;
    lock (frameTimes)
    {
        frameTimes.Add(tNow);
        frameSizes.Add(rawSize);
        if (frameCount % SampleEncodeEvery == 0)
        {
            try
            {
                using var ms = new MemoryStream();
                EncodeJpeg(bmp, ms, JpegQuality);
                encodedJpegSizes.Add((int)ms.Length);
            }
            catch { }
        }
        frameCount++;
    }
};

device.VideoSourceError += (object sender, VideoSourceErrorEventArgs e) =>
{
    Console.Error.WriteLine($"  [device error] {e.Description}");
};

Console.WriteLine($"Press Enter to start a {RunSeconds}-second capture …");
Console.ReadLine();

Console.WriteLine($"Capturing for {RunSeconds} s.");
device.Start();
Thread.Sleep(TimeSpan.FromSeconds(RunSeconds));
device.SignalToStop();
device.WaitForStop();
Thread.Sleep(200);   // settle

// ──────── Step D — analyze + print ────────
Console.WriteLine();
Console.WriteLine($"First-frame format: {firstWidth}×{firstHeight}  pixelFormat={firstPixelFormat}");
Console.WriteLine($"Frame count: {frameCount}  over {RunSeconds} s  → ~{frameCount / (double)RunSeconds:F1} FPS");
Console.WriteLine();

if (frameCount == 0)
{
    Console.Error.WriteLine("FAIL: zero frames captured.  Device exists + opened but never fired NewFrame.");
    Console.Error.WriteLine("       Common causes: another app holds the cam (Teams, Zoom, OBS), driver wedged, privacy setting denies access.");
    return 5;
}

if (frameCount < 2)
{
    Console.WriteLine("Only one frame — cannot compute jitter.  Capture pipeline is alive but very slow.");
}
else
{
    // Per-frame inter-arrival.
    var intervals = new List<long>(frameCount);
    for (int i = 1; i < frameTimes.Count; i++)
        intervals.Add(frameTimes[i] - frameTimes[i - 1]);
    double mean = intervals.Average();
    double sumSq = intervals.Sum(d => (d - mean) * (d - mean));
    double stddev = Math.Sqrt(sumSq / intervals.Count);
    long min = intervals.Min();
    long max = intervals.Max();
    Console.WriteLine($"Inter-frame interval (ms):  mean={mean:F1}  stddev={stddev:F1}  min={min}  max={max}");
    Console.WriteLine($"  → mean FPS = {1000.0 / mean:F1}");
    Console.WriteLine($"  → jitter   = {(stddev / mean) * 100:F0}% of mean interval");
    Console.WriteLine();
}

// Raw + encoded size summary.
if (frameSizes.Count > 0)
{
    long rawMin = frameSizes.Min(), rawMax = frameSizes.Max();
    double rawMean = frameSizes.Average();
    Console.WriteLine($"Raw frame size (bytes):     mean={rawMean / 1024:F0} KB  min={rawMin / 1024} KB  max={rawMax / 1024} KB");
}
if (encodedJpegSizes.Count > 0)
{
    int jpgMin = encodedJpegSizes.Min(), jpgMax = encodedJpegSizes.Max();
    double jpgMean = encodedJpegSizes.Average();
    Console.WriteLine($"JPEG size (Q{JpegQuality}, bytes):     mean={jpgMean / 1024:F1} KB  min={jpgMin / 1024} KB  max={jpgMax / 1024} KB  samples={encodedJpegSizes.Count}");
    // Approximate bandwidth at observed FPS.
    double observedFps = frameCount / (double)RunSeconds;
    double kbpsApprox = jpgMean * observedFps * 8 / 1024;
    Console.WriteLine($"  → at {observedFps:F0} FPS Tier 1 sustained bandwidth ≈ {kbpsApprox:F0} kbps = {jpgMean * observedFps / 1024:F0} KB/s per stream");
}
Console.WriteLine();

// ──────── Step E — print sample frame-arrival trace (first 30 frames) ────────
int traceCount = Math.Min(30, frameTimes.Count);
Console.WriteLine($"Per-frame arrival trace (first {traceCount} of {frameTimes.Count} frames):");
for (int i = 0; i < traceCount; i++)
{
    long delta = i > 0 ? (frameTimes[i] - frameTimes[i - 1]) : 0;
    string bar = Bar(delta, 100);
    Console.WriteLine($"  [{i,3}]  t={frameTimes[i],5} ms  Δ={delta,4} ms  {bar}");
}
if (frameTimes.Count > traceCount)
    Console.WriteLine($"  … ({frameTimes.Count - traceCount} more)");
Console.WriteLine();

// ──────── Step F — verdict guidance ────────
Console.WriteLine("Verdict guidance (for dev to fill in README):");
Console.WriteLine("  · If observed FPS ≥ 80% of target capability AND stddev/mean ≤ 30% → CAPTURE WORKS, library suitable for Tier 1.");
Console.WriteLine("  · If observed FPS ≥ 50% of target AND no NewFrame stalls > 500 ms → PARTIAL, proceed with 10 FPS Tier 1 cap.");
Console.WriteLine("  · If observed FPS < 30% of target OR a single Δ > 500 ms → INVESTIGATE driver / try alternative library before Tier 1.");
Console.WriteLine();
Console.WriteLine("Library notes:");
Console.WriteLine("  · Tier 1 (teacher cam): already uses AForge.Video.DirectShow + System.Drawing JPEG (Teacher.csproj refs 2.2.5).  If this spike works, ship Tier 1 with the existing path.");
Console.WriteLine("  · Tier 2 (student cam): needs the same package added to Student.Agent.csproj.  Spike validates the library on the dev hardware that will host the student agent.");
Console.WriteLine("  · Tier 3 (gallery): cam path stays AForge; bandwidth scale is the gate, not library — see docs/conference-tier3-design.md.");
Console.WriteLine();

return 0;


// ──────── Helpers ────────

static void EncodeJpeg(Bitmap bmp, Stream output, long quality)
{
    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
    using var prm = new EncoderParameters(1);
    prm.Param[0] = new EncoderParameter(Encoder.Quality, quality);
    bmp.Save(output, codec, prm);
}

static string Bar(long valueMs, long fullScaleMs)
{
    // Map 0..fullScaleMs → 0..40 char bar.
    long clamped = Math.Max(0, Math.Min(fullScaleMs, valueMs));
    int width = (int)((double)clamped / fullScaleMs * 40);
    return new string('█', width);
}
