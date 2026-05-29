# Conference Mode — Implementation Plan (Phase 15-B through F)

**Status:** investigation. Per-phase step lists for the Conference shell
implementation primers.
**Parent:** [`conference-mode-ux-architecture.md`](./conference-mode-ux-architecture.md).
**Sibling:** [`conference-mode-ui-mockups.md`](./conference-mode-ui-mockups.md).
**Last updated:** 2026-05-29.

This doc is the per-phase work plan. The five phases (B through F) ship
in order; each ends with a 2-PC (B/C/D/E) or 5-PC (F) validation gate.

```
Phase 15-A  →  3 design docs (this doc + 2 siblings)        DONE
Phase 15-B  →  mode toggle + skeleton                        ~1.5 hr   MVP gate
Phase 15-C  →  gallery + active-speaker + pin                ~2.0 hr   MVP
Phase 15-D  →  toolbar + chat sidebar + participants         ~1.5 hr   MVP ship-ready
Phase 15-E  →  raise hand + reactions                        ~1.0 hr   polish
Phase 15-F  →  SFU backend (active-speaker hi-res + thumbs)  ~3-4 hr   scale gate
```

Total MVP (B+C+D) = **~5 hr Claude Code + ~1 hr dev validation**.

---

## Phase 15-B — Mode toggle + skeleton (~1.5 hr)

### Goal
Toggle in the teacher's header pill swaps `CurrentMainView` between
`StudentGrid` and `Conference`. Clicking "Start Conference" in the
empty gallery view broadcasts `ConferenceStart` (0x0670); students
auto-spawn `ConferenceGalleryWindow`. Clicking "End" broadcasts
`ConferenceEnd` (0x0671); students close the window.

### Pre-flight
- [ ] Phase 14-B Tier 1 committed and merged (already true on
      `ui-rewrite`).
- [ ] Dev confirms direction: ships as Classroom-default-with-Conference-
      toggle rather than as a separate product.

### Step 1 — Wire protocol (2 new codepoints + 1 DTO)

**Files:** `MessageType.cs`, `Messages.cs`, `EnvelopeWireCompatTest`.

```csharp
// MessageType.cs (append under 14-B block)

// Phase 15-B (MVP): Conference mode — session lifecycle.  Reliable; small;
// must arrive in order.  Routes via _reliableOutbox.
ConferenceStart = 0x0670,   // T→all, carries ConferenceSessionId Guid
ConferenceEnd   = 0x0671,   // T→all
```

```csharp
// Messages.cs

[MessagePackObject]
public class ConferenceStartMessage
{
    /// <summary>Per-session Guid; reused as TargetGroupId for mic / cam
    /// frame routing within the conference (mode-exclusive with 13-D
    /// group voice — see ux-architecture § 5 risk #10).</summary>
    [Key(0)] public Guid SessionId { get; set; }
    [Key(1)] public string HostName { get; set; } = "";
    /// <summary>UTC ms timestamp; teacher's "started at" anchor.</summary>
    [Key(2)] public long StartedAtMs { get; set; }
}
```

`ConferenceEnd` has no payload (empty envelope; teacher's SenderId
implicit).

T14 + T15 wire-compat tests appended.

**Commit:** `Phase 15-B step 1: wire protocol (0x0670/0x0671 ConferenceStart/End)`

### Step 2 — `MainViewKind.Conference` enum value + state props

**Files:** `Teacher/ViewModels/MainViewModel.cs`.

```csharp
// Extend the existing enum at line 50:
public enum MainViewKind { StudentGrid, QuizManager, Conference }

// New properties:
[ObservableProperty] private bool isInConference;            // true once session started
[ObservableProperty] private Guid conferenceSessionId;       // set on Start, cleared on End
[ObservableProperty] private DateTime? conferenceStartedAt;  // for elapsed-time chip
```

`partial void OnIsInConferenceChanged(bool value)` flips `CurrentMainView`
to `Conference` (or back to `StudentGrid`) accordingly.

**Commit:** `Phase 15-B step 2: MainViewKind.Conference enum + IsInConference state`

### Step 3 — Header mode-toggle pill (Teacher MainWindow.xaml)

**Files:** `Teacher/MainWindow.xaml`.

Insert a 2-state pill into the header right-cluster (around line 105
where the existing right-side StackPanel lives). Visual per mockup
§ 1. Click toggles `IsInConference` but DOES NOT broadcast — that's
the "Start Conference" CTA in the empty gallery.

```xml
<!-- New pill, before the bell button -->
<Border ...>
    <StackPanel Orientation="Horizontal">
        <RadioButton GroupName="ModeToggle" Content="📚 Classroom"
                     IsChecked="{Binding IsInConference,
                       Converter={StaticResource InverseBoolConverter}, Mode=OneWay}"
                     Command="{Binding ExitConferenceModeCommand}"/>
        <RadioButton GroupName="ModeToggle" Content="📹 Conference"
                     IsChecked="{Binding IsInConference, Mode=OneWay}"
                     Command="{Binding EnterConferenceModeCommand}"/>
    </StackPanel>
</Border>
```

Mode swap collapses the 240px sidebar + 320px chat rail by binding their
column widths conditionally (or via Visibility on the surrounding Borders).

**Commit:** `Phase 15-B step 3: header mode-toggle pill + sidebar / chat collapse in Conference`

### Step 4 — `ConferenceView.xaml` skeleton

**Files:** new `Teacher/Views/ConferenceView.xaml(.cs)`.

Empty container: gradient background, large "▶ Start Conference" button
when `ConferenceSessionId == Guid.Empty`, otherwise empty gallery
placeholder.

Wired in MainWindow.xaml at the same Grid cell as QuizManagerView (line
649) with `Grid.ColumnSpan="2"` so it covers both content + rail.

Visibility:
```xml
Visibility="{Binding CurrentMainView,
              Converter={StaticResource ViewKindToVisibility},
              ConverterParameter=Conference}"
```

**Commit:** `Phase 15-B step 4: ConferenceView.xaml skeleton + Start CTA`

### Step 5 — Start / End commands + broadcast wiring

**Files:** `Teacher/ViewModels/MainViewModel.cs`, `Services/ControlServer.cs`.

```csharp
// MainViewModel
public IRelayCommand StartConferenceCommand { get; }
public IRelayCommand EndConferenceCommand { get; }

private async void StartConference()
{
    // Pre-flight: dissolve breakouts (modal confirm per architecture § 5 risk #2)
    if (Rooms.Any())
    {
        var ok = MessageBox.Show(Loc.Get("Conf_DissolveBreakoutsConfirm"), ...);
        if (ok != MessageBoxResult.OK) return;
        await App.Server.DissolveAllRoomsAsync(default);
    }

    ConferenceSessionId = Guid.NewGuid();
    ConferenceStartedAt = DateTime.UtcNow;
    IsInConference = true;
    if (App.Server != null)
        await App.Server.BroadcastConferenceStartAsync(ConferenceSessionId, default);
}

private async void EndConference()
{
    if (App.Server != null) await App.Server.BroadcastConferenceEndAsync(default);
    ConferenceSessionId = Guid.Empty;
    ConferenceStartedAt = null;
    IsInConference = false;
}
```

`ControlServer.BroadcastConferenceStartAsync` / `BroadcastConferenceEndAsync`
ride `_reliableOutbox` (one-line each — see 14-B step 2 precedent).

**Commit:** `Phase 15-B step 5: Start/End conference commands + ControlServer broadcast methods`

### Step 6 — Student-side dispatch + new `ConferenceGalleryWindow` skeleton

**Files:** `Student.Agent/MainWindow.xaml.cs`, new
`Student.Agent/ConferenceGalleryWindow.xaml(.cs)`.

In `OnIpcMessage` switch:
```csharp
case MessageType.ConferenceStart:
    Dispatcher.Invoke(() =>
    {
        if (_confWindow != null) return;
        var msg = MessagePack.MessagePackSerializer
            .Deserialize<ConferenceStartMessage>(env.Payload);
        _confWindow = new ConferenceGalleryWindow(msg.SessionId);
        _confWindow.Closed += (_, _) => _confWindow = null;
        _confWindow.Show();
    });
    break;
case MessageType.ConferenceEnd:
    Dispatcher.Invoke(() =>
    {
        _confWindow?.Close();
        _confWindow = null;
    });
    break;
```

`ConferenceGalleryWindow` in step 6 is a placeholder full-screen window
with "Conference in progress…" and a "Leave" button. Real gallery
lands in step 7 (Phase 15-C).

**Commit:** `Phase 15-B step 6: student ConferenceGalleryWindow + dispatch arms (placeholder gallery)`

### Step 7 — Localization (~6 new keys)

`Conf_StartConference`, `Conf_EndConference`, `Conf_Leave`,
`Conf_DissolveBreakoutsConfirm`, `Conf_HostLabel`, `Conf_SessionElapsedFmt`.

**Commit:** `Phase 15-B step 7: localization strings (EN+TH) for conference start/end UI`

### Step 8 — Acceptance + polish

**Commit:** `Phase 15-B step 8: Tier 15-B polish + 8-item 2-PC acceptance`

### Phase 15-B acceptance (8 items)

| # | Check | Verify |
|---|---|---|
| 1 | Mode toggle pill renders in header | Visual |
| 2 | Toggle switches MainViewKind | Sidebar + rail collapse |
| 3 | Start Conference button visible in empty gallery | Visual |
| 4 | Click Start with no breakouts → ConferenceStart broadcasts | Wireshark / log |
| 5 | Click Start with breakouts → confirm dialog → dissolve → broadcast | Wire 13-B `BreakoutDissolve` fires first |
| 6 | Force student receives ConferenceStart → ConferenceGalleryWindow opens | Visual |
| 7 | Teacher clicks End → ConferenceEnd broadcasts → student window closes | Wire + visual |
| 8 | Teacher disconnect during session → student gets auto-end | Wireshark; auto-end logic in ControlServer |

---

## Phase 15-C — Gallery view + active speaker + pin (~2 hr)

### Goal
`ConferenceGalleryView` (teacher) + `ConferenceGalleryWindow` (student)
render real participant tiles with webcam frames (existing
`CameraFrame` 0x0461 broadcast — for Tier 1 just teacher's cam) +
auto-layout per N + active-speaker accent + pin/unpin click.

### Step 1 — `ConferenceParticipantViewModel`
- ObservableObject with `EndpointId`, `DisplayName`, `IsTeacher`,
  `IsCamLive`, `IsMicLive`, `IsSpeaking` (consumes 13-D), `IsHandRaised`,
  `JpegFrame` (BitmapImage), `IsPinned`.

### Step 2 — `ConferenceParticipants` ObservableCollection
On `MainViewModel`. Hydrated from `Students` + a `selfParticipant` for the
teacher. Synced when `IsInConference` flips.

### Step 3 — Auto-layout grid
`ConferenceGalleryView.xaml` uses `UniformGrid` with row/col bound to a
converter that picks `(rows, cols)` from `Participants.Count`. Breakpoints
per mockup § 13.

### Step 4 — Tile DataTemplate
Per mockup § 12: webcam image OR placeholder, bottom-left name + indicators,
top-right hand-raise badge, border accent on speaker/pinned.

### Step 5 — Frame routing
Teacher cam: existing `CameraFrame` 0x0461 → MainViewModel routes to
`selfParticipant.JpegFrame`. Student cam: NOT in 15-C (deferred to 15-F
or a Phase 14-C bundle).

### Step 6 — Active speaker
Subscribe `App.Server.MicStateUpdated` (13-D event). 1.5 s hysteresis.
Sets `IsSpeaking` on the matching participant. Tile border accent
animates in/out.

### Step 7 — Pin / spotlight
Click tile → toggle `IsPinned`. Layout converter detects 1 pinned →
switches to filmstrip layout (mockup § 8).

### Step 8 — Student-side mirror
`ConferenceGalleryWindow` is the same XAML + view model; just a Window
host instead of a UserControl.

### Step 9 — Localization + polish
~4 new keys (`Conf_NoCam`, `Conf_PinTile`, `Conf_UnpinTile`,
`Conf_SpeakingLabel`).

### Phase 15-C acceptance (8 items)
Cam frames render per-tile, active speaker accent fires within 2 s,
pin/unpin layout swap is clean, layout adapts at every breakpoint
boundary, no leak after 10 min cycle.

---

## Phase 15-D — Toolbar + chat sidebar + participants (~1.5 hr)

### Goal
The Meet-style bottom toolbar + the slide-in chat sidebar + participants
panel. Existing services reused; only the UI shell is new.

### Step 1 — `ConferenceToolbar.xaml` UserControl
Per mockup § 9. Binds to:
- `MicButtonText` + `ToggleMicCommand` (existing)
- `CameraButtonText` + `ToggleCameraCommand` (existing)
- `ShareScreenButtonText` + `ShareScreenCommand` (existing)
- `ToggleChatSidebarCommand` (new)
- `ToggleHandRaiseCommand` (placeholder; wired in 15-E)
- `OpenMoreMenuCommand` (popup)
- `EndConferenceCommand` (15-B)

### Step 2 — Slide-in chat sidebar
Reuses Phase 9.8 `ChatRoom` infra. New ChatRoom Guid = `ConferenceSessionId`.
Slide animation 200 ms ease-out. Overlay (not push) so gallery layout
doesn't reflow.

### Step 3 — Participants panel
`MainViewModel.Students` with conference badges. Tabs at top of sidebar
toggle Chat ↔ Participants (mirror existing right-rail tab pattern).

### Step 4 — Inline screen-share UI
When `ScreenBroadcaster.IsActive`, the gallery adds a special "Shared
Screen" tile at top of gallery (16:9, ~50% width). Other tiles
re-flow into filmstrip below.

### Step 5 — Breakout entry from Conference toolbar (mode-exclusive)
Clicking `⋮ More` → "Breakout Rooms" shows confirm: "Opening breakouts
will end the Conference. Continue?" → End Conference first, then open
GroupManagerView.

### Step 6 — Lock / Quiz blocked
When `IsInConference == true`, `LockAllCommand` and `OpenQuizManagerCommand`
show toast and no-op (architecture § 3 table).

### Step 7 — Localization (~8 new keys)

### Phase 15-D acceptance (10 items)
All toolbar buttons round-trip working features, chat sidebar opens/closes
smoothly, participants panel updates on mic/cam state changes, screen
share appears in gallery, blocked actions show clear toasts.

---

## Phase 15-E — Raise hand + reactions (~1 hr, polish)

### Goal
Hand-raise queue + transient emoji reactions.

### Step 1 — Wire (3 new codepoints + DTO)

```
HandRaise  = 0x0672   // S→T, reliable, no payload (SenderId implicit)
HandLower  = 0x0673   // S→T or T→S, reliable
Reaction   = 0x0674   // S↔T, reliable+broadcast, payload = ReactionMessage
```

```csharp
[MessagePackObject]
public class ReactionMessage
{
    [Key(0)] public string Emoji { get; set; } = "👍";  // 1-3 chars
    [Key(1)] public long ExpiresAtMs { get; set; }  // wall-clock end of 1.5 s float
}
```

T16–T18 wire-compat appended.

### Step 2 — `IsHandRaised` ObservableProperty on `ConferenceParticipantViewModel`
Already declared in 15-C step 1; this step wires the dispatch + toolbar
`✋` toggle.

### Step 3 — Hand-raise queue
Teacher's participants panel shows hand-raised participants at top,
ordered by `HandRaisedAt` timestamp. "Recognize" button per row →
broadcasts `HandLower` for that participant + speaks their name?
(Deferred; just lowers.)

### Step 4 — Reactions popup in ⋮ More
5 emoji: 👍 ❤️ 😂 😮 😢. Click → broadcasts `Reaction { Emoji = ... }`.

### Step 5 — Floating reaction animation
On `Reaction` dispatch, find the matching participant tile, spawn a
Path + DoubleAnimation that drifts up + fades over 1.5 s. Tile
indicators (mic / cam / hand) coexist.

### Step 6 — Localization (~4 new keys)

### Phase 15-E acceptance (5 items)
Hand raise badge appears on tile + in panel, queue ordering correct,
reactions float on the right tile (not on a random tile), 5 reactions
in 1 s don't crash the renderer.

---

## Phase 15-F — Tier 3 SFU backend (~3-4 hr, deferred)

### Trigger criteria
Customer hits >20 simultaneous Conference participants AND reports
bandwidth pressure on teacher egress.

### Scope
The exact work scoped in
[`conference-tier3-design.md`](./conference-tier3-design.md) (Phase 14-A
Tier 3): source-side dual-encode OR teacher-relay downscale, active-
speaker selective forwarding, `WebcamPresenterSet` 0x0656.

### Pre-flight gate
Per Phase 14-A Tier 3 § 2: customer commits to gigabit switched LAN
verified by `iperf3`, 5-PC dry-run with Wireshark, customer PDPA
sign-off on whole-class cam disclosure.

### Why deferred from MVP
CamSpike actual ~148 KB/s per stream is ~50% of the architecture doc's
budget. At N=20 in Conference with 50% cam-on adoption (typical
classroom): 10 cams × 20 receivers × 150 KB/s = 30 MB/s = ~240 Mbps
egress. Fits gigabit. SFU only required as the customer scales.

---

## Cross-phase dependencies

```
Phase 15-B  ┐
            ├─→ Phase 15-C  ┐
                            ├─→ Phase 15-D  ┐
                                            ├─→ Phase 15-E (parallel-OK after D)
                                            │
                                            └─→ Phase 15-F (independent; can
                                                            start any time after
                                                            customer trigger)
```

15-B is the gate. 15-C and 15-D can ship in either order if the dev
prefers — they're parallel-independent — but the recommended order is
C then D so the gallery is real before the toolbar appears on top of it.
15-E is pure polish on top of B+C+D. 15-F is independent and only
shipped when the customer hits the scale trigger.

---

## File list (cumulative — files touched across all phases)

### NEW files (created across 15-B through E)

| File | Phase | Purpose |
|---|---|---|
| `Teacher/Views/ConferenceView.xaml(.cs)` | 15-B | Embedded gallery view |
| `Teacher/Views/ConferenceToolbar.xaml(.cs)` | 15-D | Meet-style bottom toolbar |
| `Teacher/Views/ConferenceChatSidebar.xaml(.cs)` | 15-D | Slide-in panel |
| `Teacher/Views/ConferenceParticipantsPanel.xaml(.cs)` | 15-D | Participants list |
| `Teacher/ViewModels/ConferenceParticipantViewModel.cs` | 15-C | Per-tile state |
| `Student.Agent/ConferenceGalleryWindow.xaml(.cs)` | 15-B/C | Student-side full-screen window |

### MODIFIED files (cumulative)

| File | Phases | What changes |
|---|---|---|
| `Shared/Protocol/MessageType.cs` | 15-B (×2), 15-E (×3) | Append 0x0670–0x0674 |
| `Shared/Protocol/Messages.cs` | 15-B, 15-E | `ConferenceStartMessage`, `ReactionMessage` |
| `Shared/Localization/LocalizationData.cs` | 15-B/C/D/E | ~22 new keys total (EN+TH) |
| `Teacher/MainWindow.xaml` | 15-B | Header mode-toggle pill + sidebar/rail Visibility |
| `Teacher/ViewModels/MainViewModel.cs` | 15-B/C/D | `MainViewKind.Conference`, conference props, commands |
| `Teacher/Services/ControlServer.cs` | 15-B/E | Broadcast helpers + dispatch arms |
| `Student.Agent/MainWindow.xaml.cs` | 15-B/C/E | Dispatch arms for new MessageTypes |
| `tools/EnvelopeWireCompatTest/Program.cs` | 15-B (T14/15), 15-E (T16/17/18) | New round-trip tests |

### UNTOUCHED (called but not modified)

`CameraBroadcastService`, `MicBroadcaster`, `VoiceMixer`, `PttKeyboardHook`,
`ScreenBroadcaster`, `ChatService`, `TcpControlServer`, `Envelope`,
all existing services from Phase 9.5 / 11-B / 13-B/C/D / 14-B.

---

## Validation rig requirements

| Phase | PCs | Why |
|---|---|---|
| 15-B | 2 (sirin teacher + force student) | Start/End round-trip, window spawn/close |
| 15-C | 2 (same) | Cam frames render, active speaker, pin |
| 15-D | 2 (same) | Toolbar buttons + chat sidebar + participants |
| 15-E | 2 (same) | Hand raise + reaction animations |
| 15-F | 5+ | SFU bandwidth + active-speaker selection at scale |

---

## Risks per phase (summary; full list in architecture doc § 5)

| Phase | Top risk | Mitigation |
|---|---|---|
| 15-B | Header pill collides with existing right-cluster cramped space | Pre-flight: measure pixel-budget at 1366 width; shift bell+cog if needed |
| 15-C | Active-speaker hysteresis tuning misfires on noisy classrooms | Configurable threshold; default 1.5 s with VAD threshold from 13-D |
| 15-D | Chat sidebar overlay + screen-share tile compete for screen real estate | Sidebar overlay (not push) so gallery doesn't reflow; share tile takes top 50% |
| 15-E | Reaction spam (a student spam-clicking 👍) | Rate-limit per sender (1 reaction / 2 s) at ControlServer relay |
| 15-F | Customer LAN doesn't actually meet gigabit-switched gate | Pre-flight verification via `iperf3`; deferred recommendation if fails |

---

## What 15-A explicitly does NOT include

- Any code changes (per primer constraint).
- Wire codepoint additions to `Messages.cs` (proposed only; added in 15-B step 1).
- UI refactor of existing MainWindow shell.
- Refactor of existing services.
- Window-style or theme changes (use existing tokens).
- Mobile / tablet support.
- Background blur / live captions / live transcription.
- Recording (deferred to v2, document in 15-D toolbar with disabled
  Record button).

---

## After Phase 15-A

1. Dev reviews 3 docs (this + the 2 siblings).
2. Decides:
   - Proceed to Phase 15-B (~1.5 hr) — recommended next step.
   - Or revise scope (e.g., if customer wants Conference-as-default-
     mode rather than toggle-from-Classroom).
3. Optionally adjust tier sizes based on review.

After 15-B, the dev validates the 8-item acceptance. If clean, proceed
to 15-C in the same or next session. MVP ship (B+C+D) targeted for a
~5 hr development session + 1 hr validation.
