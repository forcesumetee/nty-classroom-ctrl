// Phase 11-B inc3 STEP 3 — capture-timing harness.
//
// Runs each IScreenCapturer (GDI primary, DXGI primary) for N frames, measures
// avg capture+resize ms/frame, and writes 2-3 sample PNGs so the dev can
// eyeball correctness: cursor present, BGRA channel order, resize quality,
// no tearing/garbage.
//
// Run from the repo root:
//   dotnet run --project tools\CaptureTimingHarness\CaptureTimingHarness.csproj -c Release
//
// The first DXGI tick warms the duplication pipeline so the first measurement
// often shows the cold-start cost; we report a separate "first-frame" timing
// for context.

using ClassroomCtrl.Shared.Capture;
using System.Diagnostics;
using System.Drawing.Imaging;

const int Width  = 1920;
const int Height = 1080;
const int FrameCount = 60;
const int WarmupFrames = 5;
const string SampleDir = @"C:\ClassroomCtrl\tools\capture-samples";

Directory.CreateDirectory(SampleDir);

Console.WriteLine($"== Phase 11-B inc3 capture-timing harness ==");
Console.WriteLine($"   target: {Width}x{Height}, {FrameCount} frames per backend (+{WarmupFrames} warm-up)\n");

var gdi = RunBackend("GDI",  new GdiScreenCapturer(ScreenCaptureSource.Primary),  prefix: "gdi");
DxgiResult dxgi;
try
{
    dxgi = RunBackend("DXGI", new DxgiScreenCapturer(ScreenCaptureSource.Primary), prefix: "dxgi");
}
catch (Exception ex)
{
    Console.WriteLine($"\nDXGI initialisation FAILED: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"This dev box can't use DXGI Desktop Duplication — typical causes:");
    Console.WriteLine($"  • Running over RDP (RDP sessions can't duplicate the local console)");
    Console.WriteLine($"  • No discrete or iGPU adapter (rare)");
    Console.WriteLine($"  • Driver too old (pre-Windows 8 / WDDM 1.2)");
    Console.WriteLine($"In production, the factory's try/catch would fall back to GDI permanently.");
    dxgi = new DxgiResult(false, 0, 0, "init-failed");
}

Console.WriteLine($"\n==================== SUMMARY ====================");
Console.WriteLine($"  GDI   avg/frame ......... {gdi.AvgMs,7:F2} ms  (first frame {gdi.FirstMs:F2} ms)  [{gdi.Description}]");
if (dxgi.Ok)
    Console.WriteLine($"  DXGI  avg/frame ......... {dxgi.AvgMs,7:F2} ms  (first frame {dxgi.FirstMs:F2} ms)  [{dxgi.Description}]");
else
    Console.WriteLine($"  DXGI  ................... SKIPPED ({dxgi.Description})");
if (dxgi.Ok && dxgi.AvgMs > 0)
    Console.WriteLine($"\nCAPTURE-TIMING: GDI={gdi.AvgMs:F2}ms  DXGI={dxgi.AvgMs:F2}ms  speedup={gdi.AvgMs / dxgi.AvgMs:F2}x");
else
    Console.WriteLine($"\nCAPTURE-TIMING: GDI={gdi.AvgMs:F2}ms  DXGI=n/a");

Console.WriteLine($"\nSample PNGs in {SampleDir}\\");
return 0;


// ─── per-backend test loop ───
static DxgiResult RunBackend(string label, IScreenCapturer capturer, string prefix)
{
    Console.WriteLine($"─────── {label} backend ───────");
    Console.WriteLine($"   description: {capturer.Description}");

    int produced = 0;
    int nullFrames = 0;
    double firstMs = 0;
    double totalMs = 0;
    var sw = new Stopwatch();
    using (capturer)
    {
        // Warm up — first frame typically pays a cold-start cost that distorts
        // the average if included in the measurement.  Measure it separately.
        for (int i = 0; i < WarmupFrames; i++)
        {
            sw.Restart();
            using var bmp = capturer.Capture(Width, Height);
            sw.Stop();
            if (i == 0) firstMs = sw.Elapsed.TotalMilliseconds;
            // Static screen may return null on DXGI; nudge the screen a bit by
            // sleeping a small interval (50ms) between warm-up frames so the
            // user's mouse / animation typically produces a new frame.
            Thread.Sleep(50);
        }

        // Timed loop — measure capture+resize cost per frame.
        for (int i = 0; i < FrameCount; i++)
        {
            sw.Restart();
            using var bmp = capturer.Capture(Width, Height);
            sw.Stop();
            totalMs += sw.Elapsed.TotalMilliseconds;
            if (bmp == null)
            {
                nullFrames++;
            }
            else
            {
                produced++;
                // Save 3 sample PNGs at frames 0, FrameCount/2, FrameCount-1
                // so the dev can eyeball them (cursor present, colours right,
                // no tearing).
                if (i == 0 || i == FrameCount / 2 || i == FrameCount - 1)
                {
                    var path = Path.Combine(SampleDir, $"{prefix}-frame-{i:D2}.png");
                    bmp.Save(path, ImageFormat.Png);
                }
            }
            Thread.Sleep(15);  // approximate the inc3 4 FPS interval / 6 FPS = ~166ms;
                               // 15ms is intentionally tight to stress-test DXGI's
                               // AcquireNextFrame timeout path.
        }
    }

    double avgMs = produced > 0 ? totalMs / FrameCount : 0;
    Console.WriteLine($"   frames produced:  {produced}/{FrameCount} (null on static screen: {nullFrames})");
    Console.WriteLine($"   first-frame ms:   {firstMs:F2}");
    Console.WriteLine($"   avg ms/frame:     {avgMs:F2}");

    return new DxgiResult(true, avgMs, firstMs, capturer.Description);
}

record DxgiResult(bool Ok, double AvgMs, double FirstMs, string Description);
