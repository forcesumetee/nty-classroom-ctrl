# Conference Parity — Tier 3 Design (Phase 16-D)

**Status:** investigation (Phase 16-A). Step-by-step plan for Phase 16-D
(Host vs Participant role model + UI permission gating).
**Parent:** [`conference-parity-architecture.md`](./conference-parity-architecture.md).
**Siblings:** [`conference-parity-tier1-design.md`](./conference-parity-tier1-design.md),
[`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md).
**Last updated:** 2026-05-29.

---

## Goal

After Phase 16-D, the Conference UI surface is fully role-aware:

- Teachers (Host role) see the existing toolbar / sidebar with all
  admin actions including Recognize, End-for-All, Mute Others.
- Students (Participant role) see the same toolbar / sidebar but with
  admin actions hidden — they see only their self-controls (mic / cam
  / chat / hand / react / Leave).

No new wire codes this round. Existing 0x0641 MicMuteRequest (13-D)
covers Mute-Others reuse; 0x0671 ConferenceEnd (15-B) covers End-for-All.

Estimated effort: ~1 hr Claude Code time. Per-step commits.

## Pre-flight

- [ ] Phase 16-B + 16-C landed (Shared.Wpf hosts the gallery + toolbar
      + sidebar; peer cam routing works).
- [ ] `IConferenceRole` / `HostRole` / `ParticipantRole` interfaces
      defined in 16-B step 1 (Shared.Wpf/ViewModels/).
- [ ] `IConferenceShellViewModel` exposes `Role` property.

## Capability Matrix (reference)

| Element | Host visible | Participant visible | Wired by |
|---|---|---|---|
| 🎙 mic toggle | ✅ | ✅ | existing |
| 📷 cam toggle | ✅ | ✅ | 16-C step 3 (student-side) |
| 🖥 share screen | ✅ | ✅ | existing 11-B (student needs Conference-mode wire-up, see Risks) |
| 💬 chat sidebar | ✅ | ✅ | existing 15-D |
| ✋ raise hand | ✅ | ✅ | existing 0x0110 (student-side) |
| ⋮ reactions popup | ✅ | ✅ | existing 15-E |
| 📞 End / 🚪 Leave | "End" verb | "Leave" verb | end vs leave UX divergence |
| Recognize button on hand queue row | ✅ | ❌ | existing 15-E |
| Mute Others (host action) | ✅ | ❌ | 13-D 0x0641 MicMuteRequest |
| Force Cam Toggle | ❌ (v2) | ❌ | deferred |
| Pin for All (Zoom spotlight) | ✅ | ❌ | NEW — see § Open Question |
| Manage Participants (kick) | ❌ (v2) | ❌ | deferred |

End vs Leave divergence: same `LeaveOrEndConferenceCommand` on the
interface; toolbar text + icon driven by `Role.CanEndForAll`:
- Host: "📞 End" + Conf_EndConference / red Danger style
- Participant: "🚪 Leave" + Conf_Leave / lighter Danger style

## Step-by-step (4 steps + 1 acceptance)

### Step 1 — Role + ShellVM interfaces materialized

**Files:** `Shared.Wpf/ViewModels/IConferenceRole.cs`,
`Shared.Wpf/ViewModels/IConferenceShellViewModel.cs`,
`Shared.Wpf/ViewModels/{HostRole, ParticipantRole}.cs`.

Per the architecture doc § 5 sketch:

```csharp
public interface IConferenceRole
{
    bool CanToggleOwnMic        { get; }
    bool CanToggleOwnCam        { get; }
    bool CanSendChat            { get; }
    bool CanSendReaction        { get; }
    bool CanRaiseOwnHand        { get; }
    bool CanLeave               { get; }
    bool CanMuteOthers          { get; }
    bool CanEndForAll           { get; }
    bool CanRecognizeHand       { get; }
    bool CanPinForAll           { get; }
    bool CanForceCamToggle      { get; }
    bool CanManageParticipants  { get; }
}
```

`HostRole` returns `true` for all; `ParticipantRole` returns `true`
only for the self-control block.

```csharp
public interface IConferenceShellViewModel
{
    IConferenceRole Role { get; }
    ConferenceGalleryViewModel ConferenceGallery { get; }
    // Self-controls — both roles bind these.
    IRelayCommand ToggleMicCommand { get; }
    IRelayCommand ToggleCameraCommand { get; }
    IRelayCommand ShareScreenCommand { get; }
    IRelayCommand ToggleConferenceSidebarCommand { get; }
    IRelayCommand ShowConferenceHandQueueCommand { get; }
    IRelayCommand<string?> SendReactionCommand { get; }
    IRelayCommand LeaveOrEndConferenceCommand { get; }
    // Host-only — gated by role visibility on UI; commands still
    // exist on the interface but are CanExecute=false for Participant.
    IRelayCommand<ConferenceTileViewModel?> RecognizeHandCommand { get; }
    IRelayCommand<ConferenceTileViewModel?> MuteParticipantCommand { get; }   // NEW in 16-D step 2
    IRelayCommand<ConferenceTileViewModel?> PinForAllCommand { get; }         // see Open Question
}
```

Teacher's `MainViewModel` already exposes most of these commands;
implement the interface by aliasing + adding the new ones (Mute,
PinForAll). Student.Agent's `StudentConferenceShellViewModel`
(introduced in 16-B step 7) implements with stubs that no-op for
host-only commands.

**Commit:** `Phase 16-D step 1: IConferenceRole + HostRole + ParticipantRole + IConferenceShellViewModel`

### Step 2 — Wire MuteParticipant via existing 13-D 0x0641

**Files:** `Teacher/ViewModels/MainViewModel.cs`,
`Teacher/Services/ControlServer.cs` (already has `SendMicMuteRequestAsync`).

Add:
```csharp
public IRelayCommand<ConferenceTileViewModel?> MuteParticipantCommand { get; }

MuteParticipantCommand = new RelayCommand<ConferenceTileViewModel?>(async tile =>
{
    if (tile == null || tile.IsSelf) return;
    if (App.Server == null) return;
    try
    {
        await App.Server.SendMicMuteRequestAsync(
            tile.EndpointId,
            muted: true,
            reason: Loc.Get("Conf_MutedByHostReason", "Muted by host"),
            CancellationToken.None);
    }
    catch (Exception ex) { AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt"), ex.Message)); }
});
```

Wire a "🔇 Mute" button to the participants-tab row alongside the
Recognize button (host-only visibility via
`Role.CanMuteOthers`).

**Commit:** `Phase 16-D step 2: MuteParticipantCommand reuses 13-D MicMuteRequest`

### Step 3 — Toolbar + Sidebar visibility gating

**Files:** `Shared.Wpf/Conference/ConferenceToolbar.xaml`,
`Shared.Wpf/Conference/ConferenceSidebar.xaml`.

Visibility bindings on elements where the role differs:

```xml
<!-- ConferenceToolbar.xaml — End vs Leave button -->
<Button Command="{Binding LeaveOrEndConferenceCommand}"
        Style="{StaticResource ConfToolbarButtonDanger}">
    <Button.Content>
        <TextBlock>
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Text" Value="🚪"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding Role.CanEndForAll}" Value="True">
                            <Setter Property="Text" Value="📞"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
    </Button.Content>
    <Button.ToolTip>
        <Binding>
            <Binding.Source>
                <!-- Switch between Conf_EndConference and Conf_Leave by role. -->
            </Binding.Source>
        </Binding>
    </Button.ToolTip>
</Button>
```

For ToolTip text — simpler to bind via a derived property
`EndOrLeaveTooltipText` on `IConferenceShellViewModel`:
```csharp
public string EndOrLeaveTooltipText
    => Role.CanEndForAll ? Loc.Get("Conf_EndConference") : Loc.Get("Conf_Leave");
```

```xml
<!-- ConferenceSidebar.xaml — Recognize button (host-only) -->
<Button Command="{Binding DataContext.RecognizeHandCommand, ...}"
        CommandParameter="{Binding}"
        Visibility="{Binding DataContext.Role.CanRecognizeHand,
                     RelativeSource={RelativeSource AncestorType=UserControl},
                     Converter={StaticResource BoolToVisibility}}"/>

<!-- ConferenceSidebar.xaml — NEW Mute Others button (host-only) -->
<Button Command="{Binding DataContext.MuteParticipantCommand, ...}"
        CommandParameter="{Binding}"
        Content="🔇"
        ToolTip="{DynamicResource Conf_MuteParticipant}"
        Visibility="{Binding DataContext.Role.CanMuteOthers,
                     RelativeSource={RelativeSource AncestorType=UserControl},
                     Converter={StaticResource BoolToVisibility}}"/>
```

Hidden, not disabled — students see a clean Participant-mode UI
without ghost-grayed buttons.

**Commit:** `Phase 16-D step 3: toolbar + sidebar role-gated visibility`

### Step 4 — Localization + role label badge

**Files:** `Shared/Localization/LocalizationData.cs`.

New keys (EN + TH):
- `Conf_MuteParticipant` — "Mute" / "ปิดไมค์"
- `Conf_MutedByHostReason` — "Muted by host" / "ครูปิดไมค์ให้"
- `Conf_RoleHost` — "Host" / "ผู้นำ"  (for the optional badge)
- `Conf_RoleParticipant` — "Participant" / "ผู้เข้าร่วม"

Optional polish: host badge on the teacher's self-tile (small 👑 chip
top-left), bound to `IsSelf && IsHost`. Defer to 16-E if time-boxed.

**Commit:** `Phase 16-D step 4: localization (EN+TH) + role labels`

### Step 5 — Build + 2-PC acceptance

- Build all 3 projects clean (no new wire codes — no T-suite changes
  required).
- T1-T19 PASS (unchanged from 16-C).
- 2-PC manual:
  - Teacher toolbar shows 📞 End; Recognize + 🔇 Mute visible per row.
  - Student toolbar shows 🚪 Leave; no Recognize, no Mute, no host
    actions.
  - Student clicks Leave → window closes; teacher remains in
    Conference (no End broadcast).
  - Teacher clicks Mute on a row → student's mic state flips (13-D
    path); student gets the existing 13-D Mute toast.
- No regression in 16-B / 16-C.

**Commit:** `Phase 16-D step 5: build + 2-PC acceptance + role smoke`

## Acceptance checklist (6 items)

| # | Check | Verify |
|---|---|---|
| 1 | Teacher = HostRole; Student = ParticipantRole | ShellVM ctor + smoke |
| 2 | Student toolbar hides Recognize + Mute Others on participants tab | visual |
| 3 | Student End-button changes to "Leave" verb | visual |
| 4 | Teacher Mute Others fires 13-D 0x0641 (student mic mutes) | wire log |
| 5 | Leave (student) closes window but doesn't end session for teacher | manual |
| 6 | No regression in 15-E reaction / Recognize / Hand queue | manual |

## Open Question — Pin for All (Zoom Spotlight)

The 15-C local pin is single-user (only the clicker sees the filmstrip
layout). Zoom's "Spotlight for Everyone" makes the host's pin
authoritative for all participants. Implementation options:

**A. Reuse existing pin command, add a `BroadcastPin` flag**

Host clicks pin → emits a new 0x0673-style `SpotlightSet` envelope with
target EndpointId; all receivers update their local
`PinnedEndpointId`. Adds one wire code (~T20 wire-compat); minimal UI
change.

**B. Skip Pin-for-All in 16-D; ship local-only pin**

Host's pin is local-only (Zoom-style "Pin" rather than "Spotlight").
Participants pin their own view independently. Defer Spotlight to
v2 polish.

**Recommendation:** **B** for 16-D. Spotlight-for-everyone is a polished
feature and doesn't gate "Conference Mode shippable." Local pin already
ships from 15-C. Reduces wire-protocol surface and keeps 16-D firmly
in "UI gating" territory rather than introducing new fan-out.

Dev decides at 16-D step 1 before scope creeps.

## Risk-specific notes

- **Risk #5 (Mute via 13-D reuse):** clarified — yes, reuse 0x0641
  `MicMuteRequest`. No new wire.
- **Risk #6 (Force cam):** explicitly deferred to v2.
- **Risk #8 (Spotlight):** open question above; recommend defer to v2.
- **Risk #11 (sln conflicts):** mitigated because 16-D doesn't touch
  the sln file.

## After 16-D

Conference Mode v1.x is **shippable**:
- Symmetric UI on Teacher and Student
- Peer cam routing (everyone sees everyone)
- Host vs Participant role gating
- All MVP features wired
- Bandwidth headroom verified for ≤ 15 students gigabit
- 15-F SFU deferred until customer trigger

Optional follow-ups:
- **16-E** (~30 min): empty-state placeholders, 👑 host badge,
  Spotlight-for-Everyone wire (Option A above).
- **17-A** (~v2): co-host pattern, recording, background blur, live
  captions, force-cam.

## Cumulative implementation docs

After 16-A this doc-set joins:
- Phase 13-A breakout architecture + 3 tier designs
- Phase 14-A Conference architecture + 3 tier designs
- Phase 15-A Conference UX architecture + mockups + impl plan
- **Phase 16-A** parity architecture + 3 tier designs

Total ~14 design docs / ~250+ KB.
