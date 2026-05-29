# Conference Mode — Tier 2 Design (Mode 2: Group Video)

**Status:** sketch. **Last updated:** 2026-05-29.
**Parent:** [`conference-mode-architecture.md`](./conference-mode-architecture.md).

## 1. Scope

Mode 2 = students within a breakout group can broadcast their webcam to
other in-group members. Mirrors **Phase 13-C Tier 2 (presenter-mode
screen share)** + **Phase 13-D Tier 3 (group voice)** but for video.

What's in scope:
- Student-side webcam capture (new `StudentCameraBroadcaster`).
- Group-targeted fan-out via `Envelope.TargetGroupId` (the Phase 13-B
  field, already validated by 13-C + 13-D).
- Receiver: `GroupPeerView` (Phase 13-C window) gains a per-peer
  video tile alongside the host's screen.
- Teacher per-student cam controls: Mode (Off / PTT / AlwaysOn) +
  Force-Enable + per-group "Cams On / Off" bulk.
- Student-side cam UI: toggle, PTT-hotkey ("C" default), banner mirrors
  Tier 1.

What's NOT in scope (Tier 3):
- Full-class gallery (Mode 3).
- Active-speaker selection.
- Source dual-encode / SFU.

## 2. Pre-flight

- [ ] Tier 1 (Mode 1) acceptance passed on dev box.
- [ ] CamSpike run again on the STUDENT test rig (force PC) — confirms
      AForge works on student hardware too. Update README's "Dev box
      result" with a second row labeled "force (student)".
- [ ] Add `AForge.Video 2.2.5` + `AForge.Video.DirectShow 2.2.5` to
      `src/ClassroomCtrl.Student.Agent/ClassroomCtrl.Student.Agent.csproj`
      (mirror Teacher's package refs).

## 3. Implementation steps

### Step 1 — Wire protocol (5 new codepoints + 4 new DTOs)

**Files touched:** `MessageType.cs`, `Messages.cs`,
`tools/EnvelopeWireCompatTest/Program.cs`.

```csharp
// In MessageType.cs, append:

// Phase 14-C (Tier 2): Conference Mode — group video.  Star topology
// (host → teacher relay → group peers), routed via TargetGroupId + IsForMe.
// Mirrors Phase 13-C Tier 2 (screen) + Phase 13-D Tier 3 (voice) but for cam.
StudentGroupCameraStart  = 0x0651,   // S→T→group peers, reliable
StudentGroupCameraFrame  = 0x0652,   // S→T→group peers, lossy _outbox
StudentGroupCameraStop   = 0x0653,   // S→T→group peers, reliable
WebcamForceEnable        = 0x0654,   // T→S targeted, reliable (overt)
WebcamModeSet            = 0x0655,   // T→S targeted, reliable
```

New DTOs in `Messages.cs` per the spec in
[`conference-mode-architecture.md`](./conference-mode-architecture.md) §4.
Tests T14–T18 in `EnvelopeWireCompatTest`.

**Commit:** `"Phase 14-C step 1: wire protocol (0x0651-0x0655 group cam + force + mode set)"`

### Step 2 — `StudentCameraBroadcaster` (NEW, parallel to MicBroadcaster)

**Files added:** `src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs`.

Shape mirrors `MicBroadcaster` from Phase 13-D, but capture source is
AForge instead of NAudio.

```csharp
public class StudentCameraBroadcaster : IDisposable
{
    public bool IsCapturing { get; private set; }
    public bool IsMuted { get; set; } = true;       // default off
    public WebcamMode Mode { get; set; } = WebcamMode.Off;
    public bool IsPttDown { get; set; }
    public Guid? CurrentGroupId { get; set; }       // set by MainWindow on BreakoutAssign
    public Guid SelfEndpointId { get; set; }
    public string DisplayName { get; set; } = "";

    public event Action<byte[]>? FrameReady;        // JPEG bytes
    public event Action<WebcamStateUpdateMessage>? StateUpdateReady;
    public event Action? StateChanged;

    private VideoCaptureDevice? _device;
    private string? _moniker;
    private long _heartbeatMs;

    public bool Start(string moniker, int width = 640, int height = 480, int fps = 10)
    {
        if (IsCapturing) return false;
        try
        {
            _moniker = moniker;
            _device = new VideoCaptureDevice(moniker);
            var cap = _device.VideoCapabilities
                .OrderBy(c => Math.Abs(c.FrameSize.Width - width) + Math.Abs(c.FrameSize.Height - height))
                .FirstOrDefault();
            if (cap != null) _device.VideoResolution = cap;
            _device.NewFrame += OnNewFrame;
            _device.VideoSourceError += OnError;
            _device.Start();
            IsCapturing = true;
            EmitState();
            return true;
        }
        catch { _device = null; return false; }
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        try { _device?.SignalToStop(); _device?.WaitForStop(); } catch { }
        _device = null;
        IsCapturing = false;
        EmitState();
    }

    private void OnNewFrame(object? sender, NewFrameEventArgs e)
    {
        // Gate by state: if muted, or PTT mode + not down, drop the frame.
        if (IsMuted) return;
        if (Mode == WebcamMode.Off) return;
        if (Mode == WebcamMode.Ptt && !IsPttDown) return;
        if (!CurrentGroupId.HasValue) return;

        try
        {
            using var ms = new MemoryStream();
            EncodeJpeg(e.Frame, ms, 70);
            FrameReady?.Invoke(ms.ToArray());
        }
        catch { }
    }

    private void EmitState()
    {
        var msg = new WebcamStateUpdateMessage
        {
            DeviceAvailable = true,
            CamLive = IsCapturing && !IsMuted && (Mode != WebcamMode.Ptt || IsPttDown),
            Mode = Mode,
            LastError = "",
        };
        StateUpdateReady?.Invoke(msg);
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
```

`MainWindow.xaml.cs` lazy-inits the broadcaster on first BreakoutAssign,
wires:
- `FrameReady` → IPC `StudentGroupCameraFrame` envelope with
  `TargetGroupId = CurrentGroupId, SenderId = SelfEndpointId`.
- `StateUpdateReady` → IPC `WebcamStateUpdate` (reuses Tier 1 path).
- `StateChanged` → update `CamLiveBanner` on the student side.

Adds a `CamPttHook` mirroring `PttKeyboardHook` (Phase 13-D Step 6) —
default VK = `C` (0x43), pass-through, swallows exceptions in callback.

**Commit:** `"Phase 14-C step 2: StudentCameraBroadcaster (AForge capture + state machine + PTT-aware emit)"`

### Step 3 — Teacher relay: fan-out `StudentGroupCameraFrame` to in-group peers

**Files touched:** `Teacher/Services/ControlServer.cs` (switch arms in
`OnMessage`).

Mirror of Phase 13-C Tier 2 step 2 (screen relay) + Phase 13-D step 4
(voice routing).

```csharp
case MessageType.StudentGroupCameraStart:
case MessageType.StudentGroupCameraStop:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<StudentGroupCameraControlMessage>(env.Payload);
    // Verify sender is actually in the group they claim
    if (!_studentRoomMap.TryGetValue(env.SenderId, out var senderRoom) || senderRoom != payload.GroupId)
    {
        _logger.LogWarning("Group cam control from {Sender} for group {Group} but sender isn't in it", env.SenderId, payload.GroupId);
        break;
    }
    // Re-broadcast as group-targeted; receiver-side IsForMe(TargetGroupId) filters
    var groupEnv = Envelope.CreateGroupTargeted(env.Type, env.Payload, env.SenderId, payload.GroupId);
    await _tcp.BroadcastReliableAsync(groupEnv, ct);
    break;
}

case MessageType.StudentGroupCameraFrame:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<StudentGroupCameraFrameMessage>(env.Payload);
    if (!_studentRoomMap.TryGetValue(env.SenderId, out var senderRoom) || senderRoom != payload.GroupId)
        break;
    var groupEnv = Envelope.CreateGroupTargeted(env.Type, env.Payload, env.SenderId, payload.GroupId);
    await _tcp.BroadcastAsync(groupEnv, ct);   // _outbox (lossy)
    break;
}
```

**Commit:** `"Phase 14-C step 3: teacher relay for StudentGroupCameraStart/Frame/Stop with group-membership validation"`

### Step 4 — Receiver: `GroupPeerView` adds a cam tile per peer

**Files touched:** `Student.Agent/GroupPeerView.xaml(.cs)`,
`Student.Agent/MainWindow.xaml.cs` (dispatch arms).

`GroupPeerView` currently shows the host's screen as one big image
(Phase 13-C). Adds:
- A row at the bottom (`Height="100"`) with `ItemsControl` of per-peer cam
  tiles (small `Image` + `TextBlock` name label).
- Each tile is bound to a `PeerCamTileViewModel { SourceEndpointId,
  DisplayName, ImageSource }` — set when `StudentGroupCameraFrame`
  arrives for that source.
- Tile disappears 3 s after the last frame (idle eviction; mirror
  VoiceMixer pattern from Phase 13-D Step 5).
- Self (own cam) is NOT shown in the peer tiles row — already on the
  student's own banner.

`MainWindow.xaml.cs` dispatch arms:

```csharp
case MessageType.StudentGroupCameraStart:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<StudentGroupCameraControlMessage>(env.Payload);
    if (env.SenderId == _myEndpointId) break;   // self-filter
    Dispatcher.Invoke(() => _groupPeerView?.AddOrRefreshCamTile(payload.SourceEndpointId, payload.SourceDisplayName));
    break;
}
case MessageType.StudentGroupCameraFrame:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<StudentGroupCameraFrameMessage>(env.Payload);
    if (env.SenderId == _myEndpointId) break;
    Dispatcher.Invoke(() => _groupPeerView?.UpdateCamFrame(payload.SourceEndpointId, payload.JpegData));
    break;
}
case MessageType.StudentGroupCameraStop:
{
    var payload = MessagePack.MessagePackSerializer.Deserialize<StudentGroupCameraControlMessage>(env.Payload);
    if (env.SenderId == _myEndpointId) break;
    Dispatcher.Invoke(() => _groupPeerView?.RemoveCamTile(payload.SourceEndpointId));
    break;
}
```

**Commit:** `"Phase 14-C step 4: GroupPeerView cam tile row + per-peer JPEG render + 3s idle eviction"`

### Step 5 — Teacher controls: per-student cam Mode + Force-Enable + bulk

**Files touched:** `Teacher/GroupManagerView.xaml.cs`,
`Teacher/Services/ControlServer.cs`, `Teacher/MainWindow.xaml`
(small additions).

Teacher's per-student `[⋮]` menu in `GroupManagerView` gains a "Cameras"
submenu (mirrors the Phase 13-D "Microphones" submenu):

- **Force Enable Cam** (sends `WebcamForceEnable { Enable = true, Reason = "Teacher requested" }`)
- **Allow Cam Off** (sends `WebcamForceEnable { Enable = false }`)
- **Set Mode → Off / PTT / Always On** (sends `WebcamModeSet`)

Group-level (in the floating `GroupControllerWindow`):
- "Cams Off All" button.
- "Cams Force-On All" button (with confirmation dialog — ethics gate).

`ControlServer.SendWebcamForceEnableAsync(Guid endpointId, bool enable, string reason)`
and `SendWebcamModeSetAsync(Guid endpointId, WebcamMode mode, string hotkey)`.

Student-side `MainWindow.xaml.cs` dispatch arms apply to broadcaster,
fire balloon notification for force-enable (mirrors Phase 13-D
`MicMuteRequest`).

**Commit:** `"Phase 14-C step 5: teacher cam controls - Mode set + Force Enable + per-group bulk + balloon notify on force"`

### Step 6 — Student cam UI: toggle + PTT + banner

**Files touched:** `Student.Agent/MainWindow.xaml(.cs)`,
new `Student.Agent/CamLiveBanner.xaml(.cs)` (mirror of Teacher's,
mirror of `VoiceLiveBanner`).

Student cam toggle: button on the student-agent toolbar (next to mic
toggle from Phase 13-D). States:
- No cam → button disabled with tooltip "No webcam detected".
- Mode = Off → button shows "Enable Camera" → click → Mode = PTT (default).
- Mode = PTT → button shows "Camera: PTT (hold C)" → click → Mode = Off.
- Always-On is set only via teacher (`WebcamModeSet`) for Tier 2 — Tier 2
  doesn't expose Always-On to students (privacy default).

Banner: persistent while `CamLive == true` (mirrors `VoiceLiveBanner`).
- Green capsule: "🎥 Camera ON · Group {N}" when capturing but PTT-up.
  Wait — for cam, "capturing" means we're emitting frames. Use a simpler
  pattern: red capsule "🔴 Camera LIVE · Group {N}" while emitting.
- Banner hides when emit stops (PTT released, or Mode set to Off).

**Commit:** `"Phase 14-C step 6: student cam toolbar toggle + PTT hotkey + CamLiveBanner privacy capsule"`

### Step 7 — Cleanup + acceptance

- Verify the Tier 1 `WebcamStateUpdate` path now carries student-side
  cam-live state correctly (Tier 1 was a placeholder; Tier 2 lights it
  up).
- Verify the teacher per-student cam-state indicator (data plumbed in
  Tier 1 Step 4) now shows a green-dot chip on `StudentViewModel`
  when `CamLive == true`. (Tier 1 deferred the visual; Tier 2 wires it.)

**Commit:** `"Phase 14-C step 7: cam-live indicator chip in teacher StudentViewModel UI"`

## 4. Acceptance checklist (2-PC and 3-PC validation)

**2-PC tests** (sirin + force, both in same group):

| # | Check | How to verify |
|---|---|---|
| 1 | **Student-side cam toggle works:** click → cam starts; click → stops. | UI + log. |
| 2 | **PTT hotkey "C":** hold C → cam emits; release → stops within 1 frame. | Student banner toggles. |
| 3 | **In-group cam visible to peer:** student A enables cam → student B sees A's tile in GroupPeerView. | Visual + frame trace. |
| 4 | **Self-filter:** student A's own cam does NOT appear in their own GroupPeerView. | (Implicit — sender's MainWindow self-filters.) |
| 5 | **Idle eviction:** stop emitting → tile disappears within 3 s. | Visual. |
| 6 | **Teacher Force Enable Cam:** clicks "Force Enable" → student cam starts with balloon notification + reason text. | Student-side balloon visible. |
| 7 | **Teacher Set Mode → Off:** student cam stops; toggle button reflects new state. | UI sync. |
| 8 | **Cross-group isolation:** student in group A cannot see cam frames from student in group B. | Set up 2 groups; verify GroupPeerView has zero tiles. |
| 9 | **Cam unplugged mid-broadcast:** broadcaster stops itself; emits `CamLive=false`. | Teacher state indicator updates. |
| 10 | **Localization (EN+TH):** all new strings render correctly. | Switch language. |

**3-PC test** (sirin teacher + force + virtual student via dev VM or
second physical PC, all 3 in one group):

| # | Check | How to verify |
|---|---|---|
| 11 | **Multi-source cam mixing in GroupPeerView:** student A + student B both enable cams; student C sees TWO tiles + their own (zero self-tile). | Visual; both tiles update independently. |
| 12 | **Bulk Cams Off All:** teacher clicks group "Cams Off All" → both students stop emitting; both banners hide. | UI sync on both students. |
| 13 | **Bandwidth at 3-PC:** capture Wireshark on the teacher; verify teacher egress ≤ 1.5 MB/s with 2 student cams active. | Wireshark or Task Manager. |

**Customer-site flag:**
- Tier 2 ships with "presenter mode" only (one cam per group at a time?
  or any-member multi-cam?). **Decision: multi-cam any-member is the
  default** (voice already works that way; cam consistency makes the
  group experience coherent). Document trade-off if customer asks.

## 5. Effort estimate

- Step 1 (wire) — 15 min
- Step 2 (broadcaster + PTT hook) — 45 min
- Step 3 (teacher relay) — 20 min
- Step 4 (GroupPeerView tiles) — 45 min
- Step 5 (teacher controls + force) — 40 min
- Step 6 (student UI + banner) — 30 min
- Step 7 (cleanup + chip wire) — 20 min
- **Total: ~3.5 hours Claude Code + ~1 hour dev validation (3-PC
  ideal).**

## 6. Risks specific to Tier 2

- **CPU on student low-spec:** cam capture + JPEG + voice + screen-
  observation on the same student. CamSpike on force PC confirms
  the budget. If marginal, throttle Tier 2 cam FPS to 8 by default.
- **`GroupPeerView` layout density:** 4 cam tiles + a big screen image
  competes for screen real estate. Tier 2 design ships tiles in a
  bottom strip; if customer wants gallery-style equal layout in Tier 2,
  defer to Tier 3.
- **Force-Enable abuse risk.** The Reason field is sent verbatim to the
  student balloon; a verbose / coercive Reason could be misused.
  Mitigation: balloon shows the teacher's identity + timestamp; PDPA
  notification fires.

## 7. What can go wrong (Tier 2)

1. **AForge in Student.Agent doesn't restore at install time.** If the
   Tier 2 installer doesn't bundle the AForge DLLs (DotnetClient-style
   single-file publish), DirectShow filter graph fails at runtime.
   Verify on a clean install before merge.
2. **PTT hotkey "C" conflicts with mic-PTT "Space" cognitive load.**
   Student gets two hold-to-talk-style hotkeys. UX feedback during
   Tier 2 validation.
3. **Cross-tier interaction with Phase 13-C screen sharing:** if a
   student is hosting screen AND wants cam on simultaneously, both
   share `_outbox` (cap 16). Cap might saturate on a busy student.
   Validation: smoke-test screen+cam on same student.

## 8. Decisions deferred to dev pre-kickoff

- Multi-cam-any-member vs. presenter-mode default. This design assumes
  multi-cam-any-member; switch if customer prefers presenter-mode for
  Tier 2.
- Cam-PTT default key (`C` here; alternatives Tab, V).
- Always-On mode exposure to students. This design hides it behind
  teacher `WebcamModeSet`; surface as student toggle if customer prefers.
- `GroupPeerView` layout (bottom-strip tile row vs. equal-grid). Default
  bottom-strip for Tier 2; Tier 3 ships the gallery layout.
