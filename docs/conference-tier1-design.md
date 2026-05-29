# Conference Mode — Tier 1 Design (Mode 1: Teacher Broadcast)

**Status:** implementation-ready. **Last updated:** 2026-05-29.
**Parent:** [`conference-mode-architecture.md`](./conference-mode-architecture.md).

## 1. Scope

Mode 1 = the teacher broadcasts their webcam to all students in the
classroom, similar to a Zoom lecture-mode video. Phase 9.5
(`CameraBroadcastService` + `CameraViewWindow`) already delivers the
capture, encode, and frame plumbing. **Tier 1 closes the UX, lifecycle,
privacy, and error-handling gaps around it.**

What's in scope:
- Teacher toolbar/menu entry: "Start Conference Camera" / "Stop".
- Graceful no-cam handling (cam-toggle disabled with tooltip).
- Privacy banner on teacher screen while broadcasting (mirrors
  `VoiceLiveBanner`).
- Student emits `WebcamStateUpdate` (0x0650) at startup + on device change
  so teacher UI can pre-flight which students will receive cam (no
  per-student gate in Mode 1 — but the data is needed for Tier 2 anyway).
- Promote `CameraStart` / `CameraStop` to `_reliableOutbox` (frames stay
  on `_outbox`).
- Cam-unplugged-mid-broadcast handling (catch + emit state-update + close
  banner).

What's NOT in scope (Tier 2/3):
- Student-side cam (any direction).
- Per-group cam targeting.
- Teacher force-enable of any student's cam.
- Mode/PTT configuration.
- Active-speaker selection.

## 2. Pre-flight (must be true before Tier 1 commits)

- [x] CamSpike verdict on dev box = **WORKS** (sirin, Chicony USB
      integrated cam, 2026-05-29). 46 frames / 5 s = 9.2 FPS sustained,
      jitter stddev ~7%, no stalls > 500 ms.
- [x] CamSpike's "JPEG Q70 size mean" populated: **~16 KB/frame** →
      **~148 KB/s per stream actual**. This is **~50% of the
      architecture doc's 300–400 KB/s design budget**, so all bandwidth
      math in `conference-mode-architecture.md` § 6 has headroom; no
      tier-plan adjustments needed beyond noting the favorable result.
- [x] FPS target **locked at 10**. Driver-advertised 30 FPS is misleading
      — sustained delivery on the dev box is ~10 FPS regardless of the
      capability dump's `avg=30` line. `Start(moniker, 640, 480, 10)` in
      step 5 is the correct call shape.
- [ ] Dev confirms Phase 9.5 `CameraBroadcastService` still works post-
      Phase 13 changes (smoke-test: launch Teacher + 1 Student, call
      `CameraBroadcastService.Start(devices[0].Moniker, 640, 480, 10)`
      from the existing menu hook — student should see the cam window).
      Phase 9.5 is the load-bearing path; if it regressed during 11/12/13,
      Tier 1 starts with a "fix the regression" step before the UX work.

## 3. Implementation steps

### Step 1 — Wire protocol (NEW codepoint + DTO)

**Files touched:** `MessageType.cs`, `Messages.cs`,
`tools/EnvelopeWireCompatTest/Program.cs`.

```csharp
// In MessageType.cs, append under the Phase 13-D block:

// Phase 14-B (Tier 1): Conference Mode — teacher cam broadcast.  Closes
// Phase 9.5 UX gaps around CameraStart/Frame/Stop (0x0460-0x0462).  The one
// new code adds bidirectional cam-state visibility so the teacher UI knows
// which students have a webcam and which cam toggles to default-disable.
WebcamStateUpdate = 0x0650,   // S→T heartbeat / on-change, reliable
```

```csharp
// In Messages.cs, append:

// ───────────── Phase 14-B (Tier 1): Conference Mode ─────────────

[MessagePackObject(true)]
public class WebcamStateUpdateMessage
{
    public bool DeviceAvailable { get; set; }
    public bool CamLive { get; set; }
    public WebcamMode Mode { get; set; }
    public string LastError { get; set; } = "";
}

public enum WebcamMode : byte { Off = 0, Ptt = 1, AlwaysOn = 2 }
```

Append wire-compat tests T12 + T13 (`WebcamStateUpdateMessage` + a round-
trip with all fields populated) to `tools/EnvelopeWireCompatTest`.

**Commit:** `"Phase 14-B step 1: wire protocol (0x0650 WebcamStateUpdate + WebcamMode enum)"`

### Step 2 — Promote `CameraStart` / `CameraStop` to reliable channel

**Files touched:** `Services/ControlServer.cs`.

```csharp
// Replace the existing BroadcastCameraStartAsync / BroadcastCameraStopAsync
// implementations:

public Task BroadcastCameraStartAsync(CameraStartMessage msg, CancellationToken ct)
{
    var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
    return _tcp.BroadcastReliableAsync(  // ← was BroadcastAsync
        Envelope.Create(MessageType.CameraStart, bytes, _teacherId), ct);
}

public Task BroadcastCameraStopAsync(CancellationToken ct)
    => _tcp.BroadcastReliableAsync(  // ← was BroadcastAsync
        Envelope.Create(MessageType.CameraStop, Array.Empty<byte>(), _teacherId), ct);

// CameraFrame stays on BroadcastAsync (lossy _outbox).  Correct semantics:
// dropping a stale frame is fine; dropping a Start = black screen on
// students until teacher gives up + restarts.
```

**Commit:** `"Phase 14-B step 2: promote CameraStart/Stop to _reliableOutbox (frames stay lossy)"`

### Step 3 — Student-side `WebcamStateUpdate` emission

**Files touched:** `Student.Agent/MainWindow.xaml.cs`,
`Student.Service/ClassroomWorker.cs` (dispatch only — actual enumeration
happens in Agent which has the AForge dep when Tier 2 adds it).

For Tier 1, Agent does NOT yet reference AForge. The enumeration in
Tier 1 uses Windows' built-in `Win32_PnPEntity` WMI query OR
`DirectoryEntry` over the `MEDIA` ClassGuid `{4d36e96c-e325-11ce-bfc1-08002be10318}`,
both of which return the OS's webcam list without taking a frame. This
avoids pulling AForge into Student.Agent until Tier 2 actually captures.

```csharp
// New file: src/ClassroomCtrl.Student.Agent/WebcamDeviceWatcher.cs

using System.Management;

public class WebcamDeviceWatcher : IDisposable
{
    public bool DeviceAvailable { get; private set; }
    public event Action? DeviceChanged;

    private readonly ManagementEventWatcher _watcher;

    public WebcamDeviceWatcher()
    {
        DeviceAvailable = EnumerateOnce();
        // Watch for WM_DEVICECHANGE-equivalent via WMI; cam plug/unplug
        // fires Win32_DeviceChangeEvent.  EventType 2 = arrival, 3 = removal.
        var q = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");
        _watcher = new ManagementEventWatcher(q);
        _watcher.EventArrived += (_, _) =>
        {
            var was = DeviceAvailable;
            DeviceAvailable = EnumerateOnce();
            if (was != DeviceAvailable) DeviceChanged?.Invoke();
        };
        _watcher.Start();
    }

    private static bool EnumerateOnce()
    {
        // ClassGuid for "Camera" devices on Win10+; older systems used
        // {6bdd1fc6-810f-11d0-bec7-08002be2092f} (Image device).
        // Query both, OR the results.
        var queries = new[]
        {
            "SELECT Name FROM Win32_PnPEntity WHERE ClassGuid = '{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}'",  // Camera class (Win10+)
            "SELECT Name FROM Win32_PnPEntity WHERE ClassGuid = '{6bdd1fc6-810f-11d0-bec7-08002be2092f}'",  // Image
        };
        foreach (var q in queries)
        {
            try
            {
                using var s = new ManagementObjectSearcher(q);
                using var c = s.Get();
                if (c.Count > 0) return true;
            }
            catch { }
        }
        return false;
    }

    public void Dispose()
    {
        try { _watcher.Stop(); } catch { }
        _watcher.Dispose();
    }
}
```

`MainWindow.xaml.cs` instantiates the watcher in `OnSourceInitialized`,
emits the initial state to IPC → Service → Teacher, and re-emits on
`DeviceChanged`. The actual emit path:

```csharp
// In MainWindow.xaml.cs init:

_webcamWatcher = new WebcamDeviceWatcher();
_webcamWatcher.DeviceChanged += () => EmitWebcamState();
EmitWebcamState();

void EmitWebcamState()
{
    var msg = new WebcamStateUpdateMessage
    {
        DeviceAvailable = _webcamWatcher.DeviceAvailable,
        CamLive = false,        // Tier 1 has no student cam capture
        Mode = WebcamMode.Off,
        LastError = "",
    };
    var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
    App.Ipc?.SendAsync(MessageType.WebcamStateUpdate, bytes);
}
```

Service-side: `ClassroomWorker.cs` forwards `WebcamStateUpdate` envelopes
to the teacher (the existing IPC → TCP relay path; no per-message switch
arm needed if the relay is type-agnostic, but verify against the existing
`MicStateUpdate` Phase 13-D relay path — match its shape exactly).

**Commit:** `"Phase 14-B step 3: WebcamDeviceWatcher + WebcamStateUpdate emission (Agent + IPC relay)"`

### Step 4 — Teacher-side `WebcamStateUpdate` reception + state tracking

**Files touched:** `Services/ControlServer.cs`,
`ViewModels/MainViewModel.cs`, `ViewModels/StudentViewModel.cs`.

Mirrors Phase 13-D Tier 3 step 7 (per-student mic-indicator) but for cam:

```csharp
// In ControlServer.cs, add to the OnMessage switch:

case MessageType.WebcamStateUpdate:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<WebcamStateUpdateMessage>(env.Payload);
    _webcamStates[env.SenderId] = payload;
    WebcamStateUpdated?.Invoke(this, (env.SenderId, payload));
    break;
}

// new event:
public event EventHandler<(Guid endpointId, WebcamStateUpdateMessage state)>? WebcamStateUpdated;
private readonly Dictionary<Guid, WebcamStateUpdateMessage> _webcamStates = new();
```

`StudentViewModel` gains:

```csharp
[ObservableProperty] private bool _hasWebcam;
[ObservableProperty] private bool _camLive;
[ObservableProperty] private WebcamMode _camMode = WebcamMode.Off;
// (camera indicator chip; Tier 2 wires the visual; Tier 1 just plumbs data)
```

`MainViewModel` subscribes to `ControlServer.WebcamStateUpdated` and
routes to the right `StudentViewModel`. Pattern is identical to Phase 13-D
`OnMicStateUpdated`.

**Commit:** `"Phase 14-B step 4: teacher WebcamStateUpdate reception + StudentViewModel cam-state props"`

### Step 5 — Teacher "Start Conference Camera" UI + lifecycle

**Files touched:** `ViewModels/MainViewModel.cs`,
`MainWindow.xaml` (toolbar entry), `Services/CameraBroadcastService.cs`
(small touchups).

Teacher's main toolbar gains a "Camera" entry. Click flow:
1. If `CameraBroadcastService.IsActive == false`:
   - Call `CameraBroadcastService.EnumerateDevices()`.
   - If list is empty → toast "No webcam detected on this PC" + return.
   - Else: open a small device-picker if `count > 1`; default to
     `devices[0]` if 1.
   - `Start(moniker, 640, 480, 10)`. If returns false → toast the
     `_logger`-captured error (need to surface the exception message
     from `CameraBroadcastService` — add a `LastError` property to it).
   - On success: show privacy banner; flip toolbar entry to "Stop".
2. If `CameraBroadcastService.IsActive == true`:
   - Call `Stop()`. Hide banner. Flip toolbar entry to "Start".

Small changes to `CameraBroadcastService`:

```csharp
// Add to the class:
public string LastError { get; private set; } = "";

// In the Start catch:
catch (Exception ex) {
    _logger?.LogError(ex, "Camera Start failed");
    LastError = ex.Message;
    return false;
}

// In OnNewFrame catch:
catch (Exception ex) {
    _logger?.LogWarning(ex, "Camera frame encode failed");
    LastError = ex.Message;   // surfaces to teacher UI for diagnosis
}

// New: VideoSourceError forwarding (currently not wired; AForge fires this
// on driver-level errors, e.g. cam unplugged mid-stream).
private void OnVideoSourceError(object? sender, VideoSourceErrorEventArgs e)
{
    _logger?.LogWarning("Camera source error: {Desc}", e.Description);
    LastError = e.Description;
    // Tier 1 acceptance: cam unplugged → broadcaster stops itself + UI
    // returns to "Start" state.
    Stop();
}
```

`Start()` wires `_device.VideoSourceError += OnVideoSourceError;`.

**Commit:** `"Phase 14-B step 5: teacher Start/Stop UI + device picker + error surfacing + cam-unplugged recovery"`

### Step 6 — Privacy banner on teacher

**Files touched:** new `CamLiveBanner.xaml(.cs)` in
`src/ClassroomCtrl.Teacher/` (mirrors `Student.Agent/VoiceLiveBanner.xaml`).

```xaml
<!-- CamLiveBanner.xaml — borderless transparent topmost window, top-center -->
<Window x:Class="ClassroomCtrl.Teacher.CamLiveBanner"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        Width="320" Height="40" SizeToContent="WidthAndHeight">
    <Border x:Name="BannerBorder" CornerRadius="20" Padding="14,6"
            Background="#DC2626">
        <TextBlock x:Name="BannerText" Foreground="White" FontWeight="Bold"
                   FontFamily="Segoe UI" FontSize="13"
                   Text="🎥 Conference Camera LIVE — visible to all students"/>
    </Border>
</Window>
```

Code-behind: `PositionTopCenter()` on Loaded (copy from `VoiceLiveBanner`).
Owned by the teacher's `MainWindow`; shown by `MainViewModel` when
`CameraBroadcastService.IsActive == true`, hidden on Stop.

**Commit:** `"Phase 14-B step 6: privacy banner (CamLiveBanner) on teacher while broadcasting"`

### Step 7 — Localization strings

**Files touched:** `Shared/Localization/LocalizationData.cs`.

Add (EN + TH):
- `Conf_CamStart` = "Start Camera" / "เปิดกล้อง"
- `Conf_CamStop` = "Stop Camera" / "ปิดกล้อง"
- `Conf_NoWebcam` = "No webcam detected on this PC" / "ไม่พบกล้องเว็บแคมในเครื่องนี้"
- `Conf_CamStartFailFmt` = "Failed to start camera: {0}" / "เปิดกล้องไม่สำเร็จ: {0}"
- `Conf_BannerLive` = "🎥 Conference Camera LIVE — visible to all students"
  / "🎥 กล้องประชุมทำงานอยู่ — นักเรียนทุกคนเห็น"
- `Conf_DevicePickerTitle` = "Choose camera device" / "เลือกกล้อง"

**Commit:** `"Phase 14-B step 7: localization strings (EN+TH) for conference camera UI"`

### Step 8 — Optional: fold cleanup into step 7 if clean

Tier 1 cleanup punch list (from architecture doc §9):
- Verify Phase 9.5 path still works post-Phase 13 (smoke test).
- `MainViewModel` cam-start error surfacing (done in step 5).
- `CameraStart` / `Stop` on `_reliableOutbox` (done in step 2).
- Privacy banner (done in step 6).

If nothing's left to clean up, skip step 8 entirely.

## 4. Acceptance checklist (2-PC validation)

**Status: code complete (Phase 14-B steps 1–7, 9 commits). Awaiting dev
2-PC validation.** All 3 projects (Teacher, Student.Agent, Student.Service)
build with 0 errors; T1–T13 wire-compat suite PASS.

Run on dev box (sirin teacher + force student).

| # | Check | How to verify |
|---|---|---|
| 1 | **Teacher with cam:** "Start Camera" enables; cam window opens on student. | Click toolbar entry; observe student's `CameraViewWindow`. |
| 2 | **Privacy banner appears** while cam is live; disappears on Stop. | Visual. Banner is red capsule top-center. |
| 3 | **Teacher with no cam:** toolbar entry shows; click → toast "No webcam detected". | Disable Realtek webcam in Device Manager; relaunch Teacher; click "Start Camera". |
| 4 | **Cam held by another app** (open Camera.exe first, then Teacher Start): toast surfaces the AForge error message. | Open Win10 Camera app; then Teacher click "Start Camera". |
| 5 | **Cam unplugged mid-broadcast:** broadcaster auto-stops; banner hides; toolbar returns to "Start". | Unplug USB webcam while broadcast is live (or disable in Device Manager). |
| 6 | **Multiple cams:** device-picker dialog appears; selection takes effect. | Plug a 2nd webcam. |
| 7 | **Student-side `WebcamStateUpdate` arrives at teacher** at student startup. | Set a breakpoint in `ControlServer.OnMessage` MessageType.WebcamStateUpdate arm. |
| 8 | **`WebcamStateUpdate` re-emits when student plugs/unplugs webcam.** | Plug a webcam into the student PC mid-session. |
| 9 | **`CameraStart` arrival is reliable:** even under simulated network strain, the student sees the cam window (not a frame storm into a closed window). | (Optional) Use `clumsy` to drop 10% of packets during the Start. |
| 10 | **Concurrent screen-share + cam:** both work, no audio/screen regression. | Start screen share, then Start Camera. |
| 11 | **Localization:** TH toolbar entry + banner text renders correctly (no mojibake — see Phase 10.18 lesson). | Switch language to TH; verify all 6 new strings. |
| 12 | **Cam runs through a full 10-minute session** without memory leak. | Watch Teacher PrivateMemory in Task Manager over 10 min; bitmap leak in `OnNewFrame` would show as +50–100 MB/min. |

## 5. Effort estimate

- Step 1 (wire) — 10 min
- Step 2 (channel promote) — 5 min
- Step 3 (Agent watcher + emit) — 30 min
- Step 4 (Teacher reception) — 20 min
- Step 5 (Start/Stop UI + error surfacing) — 30 min
- Step 6 (banner) — 15 min
- Step 7 (l10n) — 10 min
- Step 8 (cleanup) — 10 min if any
- **Total: ~2 hours Claude Code + 30 min dev 2-PC validation.**

## 6. Deviation notes

- **Step 3 uses WMI device watcher, not AForge enumeration.** Rationale:
  Tier 1 doesn't capture on the student side, so pulling AForge into
  Student.Agent.csproj just for `EnumerateDevices` is wasteful. WMI's
  Camera class-Guid query returns the same set of devices. Tier 2 replaces
  the WMI path with AForge (since Tier 2 actually captures).
- **No teacher cam settings dialog in Tier 1.** Resolution/FPS hardcoded
  at 640×480 @ 10 FPS. Tier 2 polish adds a Settings dialog (with the
  CamSpike-validated capability list as the dropdown source).
- **CamSpike result locked at WORKS.** Dev box (sirin, Chicony USB
  integrated cam) delivered 9.2 FPS sustained at 148 KB/s — ~50% of
  the architecture doc's 300–400 KB/s budget. Tier 1 ships with the
  default FPS target = 10. No "PARTIAL" doc clause needed.

## 7. What can go wrong (Tier 1)

1. **Phase 9.5 regression.** If the capture loop has bitrotted since 9.5
   landed, Tier 1 starts with a fix-the-regression sub-step. Smoke-test
   first.
2. **AForge x64 / DirectShow filter graph init fails on a clean Win11
   install.** Customer reports "Start does nothing". Diagnosis = run
   CamSpike locally + read trace. If it's a Privacy Setting (Win10/11
   Settings → Privacy → Camera → "Allow apps to access camera"), surface
   in the error toast verbatim.
3. **Bitmap leak in `OnNewFrame`.** Phase 9.5's catch block doesn't
   dispose the bitmap on encode failure path. Add `using var bmp = e.Frame`
   pattern… actually no, AForge owns the frame and reuses the buffer; do
   NOT dispose. Just don't hold a reference past the callback. Verify
   with the 10-min test (#12).
4. **`_reliableOutbox` capacity 4 is tiny.** If a student is mid-file-
   transfer when teacher starts cam, the Start envelope might wait
   behind file chunks. Acceptable — it'll arrive within ~1 s — but
   document as the rationale for "Frames stay lossy, only Start/Stop
   are reliable".

## 8. Decisions deferred to dev pre-kickoff

- Toolbar entry placement (main toolbar next to "Lock screen", or in the
  Media Tools sub-menu?). Default this design assumes main toolbar.
- Device-picker dialog style (drop-down vs. modal). Modal is consistent
  with the rest of Teacher's settings; default to modal.
- Whether the privacy banner should also show on the TEACHER's own
  monitor (currently: yes, it's the teacher's confirmation that their
  cam is live). Confirm.
