# Conference Mode Parity — Architecture

**Status:** investigation (Phase 16-A). No code changes this round; design + risks only.
**Siblings:** [`conference-parity-tier1-design.md`](./conference-parity-tier1-design.md), [`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md), [`conference-parity-tier3-design.md`](./conference-parity-tier3-design.md).
**Parent context:** [`conference-mode-ux-architecture.md`](./conference-mode-ux-architecture.md) (15-A), Phase 15-B/C/D/E ship reports.
**Last updated:** 2026-05-29.

---

## 1. Why this round exists

Phase 15-E shipped Conference Mode MVP (gallery + active speaker + pin +
toolbar + chat sidebar + participants + raise hand + reactions). Dev validated
on 2-PC and found three blockers before ship:

1. **UI bug in 15-C gallery** — teacher's `ConferenceView` renders blank
   centre area with only tile bottom-edges visible ("NTY MULTIMEDIA" + "f"
   fragments). Tiles appear pushed below the viewport.
2. **Asymmetric student experience** — Phase 15-C's "smart deviation" left
   `Student.Agent/ConferenceGalleryWindow` as a single-tile teacher-cam
   viewer. Customer needs Zoom/Meet symmetry: same gallery surface on both
   sides.
3. **Peer cam = must-have, not 15-F deferral** — students need to see other
   students' webcams to call this a Conference, not a one-way broadcast.

Customer ask, refined with dev:
- Teacher = **Host** (Zoom-style admin: mute others, end-for-all, recognize,
  pin-for-all, future: force-cam)
- Student = **Participant** (self-controls: mic/cam toggle, chat, hand,
  react; sees all peers)
- Same UI surface, different capabilities (role-gated).

---

## 2. UI Bug Diagnosis

### Evidence

- **Symptom:** Conference gallery shows "1 students connected" but the
  visible area is mostly empty; only the bottom strips of tiles are
  visible at the very bottom of the gallery viewport. Fragments read
  "NTY MULTIMEDIA" (teacher self-tile's `OrganizationSubtitle` per
  [`MainViewModel.cs:109-110`](../src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs#L109-L110)) and "f" (start of "force" student name).
- **State:** Teacher self-tile + 1 student tile = N=2 in `PageTiles`.

### Layout walk-through

`MainWindow.xaml` shell:
- 3 columns: 240px sidebar / `*` content / 320px chat rail.
- Conference mode collapses sidebar + rail **Borders** but the
  **ColumnDefinitions stay 240 / * / 320** (pixel-fixed, not star).
- `ConferenceView` is slotted with `Grid.ColumnSpan="3"` (per 15-B step 3)
  so it spans the full row.
- ConferenceView has 2 rows: `*` (gallery + sidebar overlay) and `Auto`
  (toolbar, ~72 px tall).

`ConferenceGalleryView.xaml`:
- Outer `Grid Margin="12"`, rows `*` / `Auto` (page nav).
- Row 0 → inner Grid → `ItemsControl` (gallery) over `Grid` (filmstrip),
  Z-stacked with Visibility triggers on `IsPinnedView`.
- The gallery `ItemsControl` uses an `ItemsPanelTemplate` containing
  `UniformGrid` ([`ConferenceGalleryView.xaml:73-75`](../src/ClassroomCtrl.Teacher/Views/Conference/ConferenceGalleryView.xaml#L73-L75)):
  ```xml
  <UniformGrid
      Columns="{Binding PageTiles.Count,
                        Converter={StaticResource CountToColumns}}"/>
  ```

`ParticipantCountToColumnsConverter.cs:22-26` returns:
- 1 → 1 col
- 2 → 2 cols
- 3-4 → 2 cols
- 5-9 → 3 cols
- 10+ → 4 cols

For N=2 this should be a 2-col × 1-row layout — tiles side by side.

### Root-cause hypotheses (ordered by likelihood)

**H1 — UniformGrid `Columns` binding silently broken in ItemsPanelTemplate
context.**

Inside `<ItemsControl.ItemsPanel><ItemsPanelTemplate>`, the panel root's
DataContext **can** inherit from the templated parent, but there are
documented WPF cases where bindings on `UniformGrid.Columns` evaluate at
realize-time against an empty source and never re-evaluate cleanly. When
`Columns` resolves to `0`, WPF UniformGrid falls back to its
square-root layout rule (`Cols = ceil(sqrt(N))`, `Rows = ceil(N/Cols)`).

For N=2: `Cols = ceil(sqrt(2)) = 2`, `Rows = 1`. That happens to be the
*correct* layout — so H1 alone wouldn't explain the symptom for N=2.

But for **N=1 → N=2 transition** (which is what happens during a session
start: self-tile arrives first, then student tile arrives via Hello-ack
async), the panel's binding evaluation may stall at the **initial N=1
state** (Cols=1, 1 row × 1 col). When the second tile arrives the panel
still thinks Cols=1 → **2 rows × 1 col layout** → tiles stack vertically
→ tile 2 extends below the viewport → only the name strip of tile 1
visible at top, and the bottom edge "NTY MULTIMEDIA" + bottom-edge "f"
are actually the **clipped tops of the two stacked tile content panes
visible at viewport top + bottom**, depending on where the layout
truncates.

**Strength:** matches the observed symptom for transitioning N. Common
WPF ItemsPanelTemplate gotcha.

**Fix sketch (16-B step 5):** make the binding robust via explicit
RelativeSource + diagnostic logging:
```xml
<UniformGrid Columns="{Binding RelativeSource={RelativeSource AncestorType=ItemsControl},
                               Path=DataContext.PageTiles.Count,
                               Converter={StaticResource CountToColumns}}"/>
```
OR drive `Columns` from a computed `int ColumnsForPage` property on
`ConferenceGalleryViewModel` (computed in `RebuildPageTiles`,
`OnPropertyChanged` raised explicitly) so the binding path is a plain
property rather than `Count` on a collection.

**H2 — Tiles render at unconstrained height because the cam area's
`RowDefinition Height="*"` collapses to 0 inside a panel that provides
infinite vertical space.**

If the realized panel is a `StackPanel` (default ItemsControl fallback
when ItemsPanelTemplate fails to materialize), each tile is measured
with infinite available height. The inner `Grid` (tile's own layout) has
`Row 0 = "*"` (cam area) and `Row 1 = "Auto"` (name strip). With
infinite parent height, the `*` row collapses to its content's
DesiredSize, which is the placeholder StackPanel's natural height (~80 px
for the 🚫 + name). Total tile height ~110 px. With 2 tiles in vertical
stack at top of gallery, both would be visible at top — doesn't match
observed "bottom edge only" symptom unless the gallery container itself
is also being misaligned.

**Strength:** lower than H1. Would need a complete ItemsPanelTemplate
parse failure (no XAML errors reported during builds, so unlikely).

**H3 — MainWindow column widths claim space when the rail/sidebar
collapse, leaving ConferenceView narrower than expected.**

Even with `Grid.ColumnSpan="3"`, the ColumnDefinitions enforce minimum
widths: `240` (sidebar) + `*` (content) + `320 MinWidth=280` (rail) = at
least 520 px of fixed allocation. The `*` column gets `total - 520` px.
For a 1366-wide display in dev, that's 846 px of `*`. ColumnSpan=3
overlay on top of all three doesn't *recover* the 240+320 area for
content — the content's effective render width is 1366 px (full row),
**but** the inner layout assumes content is constrained to the `*`
column.

**Strength:** would cause horizontal width issues, not the observed
vertical bottom-edge crop. Likely a separate cleanup item but not the
root cause.

**H4 — `ConferenceToolbar` claims more vertical space than expected,
pushing the gallery row up.**

The toolbar's `Row 1 = "Auto"` measures against its content: a Border
with `Padding="0,8"` + 56 px circular buttons → ~72 px. Reasonable. A
quick visual inspection should rule this in or out.

**Strength:** very low.

### Recommended diagnosis path for 16-B step 5

1. Add a single line of code-behind on `ConferenceGalleryView` that
   `Debug.WriteLine`s the realized panel type + actual `Columns` value
   + tile count at Loaded time.
2. Apply the H1 fix sketch (RelativeSource binding OR computed
   `ColumnsForPage` property).
3. Confirm visually + revisit if symptom persists.

### Also-found bug in `ConferenceGalleryWindow.xaml` (Student.Agent)

Pure read of [`ConferenceGalleryWindow.xaml:80-99`](../src/ClassroomCtrl.Student.Agent/ConferenceGalleryWindow.xaml#L80-L99) reveals the bottom Leave button bar uses `Grid.Row="1"` — but the
RowDefinitions are `Auto` / `*` / `Auto`. The Leave bar should be Row 2;
today it's overlaying the tile area at Row 1. Tier 1 student users see
the Leave button overlapping the bottom of the cam tile. Trivial fix in
16-B step 6.

---

## 3. Student Current State Survey

| Capability | Today | Needed for parity |
|---|---|---|
| Window spawned on `ConferenceStart` | ✅ [`MainWindow.xaml.cs:754-770`](../src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L754-L770) | unchanged |
| Window closed on `ConferenceEnd` | ✅ [`MainWindow.xaml.cs:772-778`](../src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L772-L778) | unchanged |
| Render teacher cam tile | ✅ `UpdateFrame(jpeg)` on `_confWindow` from `CameraFrame` arm at [`MainWindow.xaml.cs:728-742`](../src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L728-L742) | unchanged |
| Render peer (other students') cam | ❌ — no per-peer routing | NEW — 16-C (peer cam fan-out) |
| Multi-tile gallery | ❌ — single 16:9 image | NEW — 16-B (shared gallery component) |
| Self-tile (own cam preview) | ❌ — student has no camera in Tier 1 | NEW — 16-C step 3 (student cam capture) |
| Toolbar: 🎙 mic toggle | ❌ — no UI; tray-icon only | NEW — 16-B (shared toolbar) |
| Toolbar: 📷 cam toggle | ❌ — student never captures cam today | NEW — 16-C + 16-B |
| Toolbar: 🖥 screen share | ⚠️ — exists per 11-B but not surfaced in Conference UI | NEW — 16-B wiring |
| Toolbar: 💬 chat | ⚠️ — student has chat in main UI; not in conference window | NEW — 16-B |
| Toolbar: ✋ raise hand | ⚠️ — exists per 0x0110; not in conference window | NEW — 16-B |
| Toolbar: ⋮ reactions | ⚠️ — receives 0x0674 (15-E `ShowReaction`) but can't send | NEW — 16-B + plumb send command |
| Sidebar: chat tab | ❌ | NEW — 16-B |
| Sidebar: participants tab | ❌ | NEW — 16-B (but student-side participants needs its own data source) |
| Active speaker accent | ❌ | NEW — 16-C (student listens to MicStateUpdate) |
| Pin/spotlight | ❌ | NEW — 16-B (local-only pin; 16-D adds host pin-for-all) |
| Receive reactions over tiles | ✅ for teacher tile only; 15-E reaction overlay at [`ConferenceGalleryWindow.xaml:53-63`](../src/ClassroomCtrl.Student.Agent/ConferenceGalleryWindow.xaml#L53-L63) | per-tile routing — 16-B + 16-C |
| Hand-raise badge on tile | ❌ | NEW — 16-B |

**Gap summary:** 12 NEW capability tickets across the three tiers
(16-B / 16-C / 16-D), with the gallery surface, toolbar, sidebar, and
participants panel all needing to be reachable by Student.Agent.

---

## 4. Shared.Wpf Design

### Problem statement

`Teacher/Views/Conference/{ConferenceTile, ConferenceGalleryView,
ConferenceToolbar, ConferenceSidebar}.xaml` + their VMs live in
`ClassroomCtrl.Teacher`. `Student.Agent` cannot reference `Teacher` (they
ship as separate processes; Teacher pulls heavy deps like AForge / NAudio
/ NReco that Student doesn't need).

Three options:

#### Option A — `ClassroomCtrl.Shared.Wpf` library (RECOMMENDED)

New project: `src/ClassroomCtrl.Shared.Wpf/`. WPF library
(`UseWPF=true`, no `OutputType`). Both Teacher and Student.Agent add a
`ProjectReference` to it. Conference UI components move there.

**Files that move:**

```
src/ClassroomCtrl.Shared.Wpf/
├── ClassroomCtrl.Shared.Wpf.csproj
├── Conference/
│   ├── ConferenceTile.xaml(+cs)         ← from Teacher/Views/Conference/
│   ├── ConferenceGalleryView.xaml(+cs)  ← from Teacher/Views/Conference/
│   ├── ConferenceToolbar.xaml(+cs)      ← from Teacher/Views/Conference/
│   └── ConferenceSidebar.xaml(+cs)      ← from Teacher/Views/Conference/
├── ViewModels/
│   ├── ConferenceTileViewModel.cs       ← from Teacher/ViewModels/ConferenceGalleryViewModel.cs
│   ├── ConferenceGalleryViewModel.cs    ← (split out from same file)
│   └── IConferenceShellViewModel.cs     ← NEW — abstracts host/participant commands
└── Converters/
    ├── BoolToVisibilityConverter.cs     ← from Teacher/Converters/
    ├── NullToVisibilityConverter.cs     ← from Teacher/Converters/
    └── ParticipantCountToColumnsConverter.cs ← from Teacher/Converters/
```

**Project file sketch:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseWPF>true</UseWPF>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>ClassroomCtrl.Shared.Wpf</RootNamespace>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClassroomCtrl.Shared\ClassroomCtrl.Shared.csproj" />
    <!-- Shared dependency only — Protocol, Localization, Branding. -->
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
    <!-- Single dep: ObservableObject + ObservableProperty source-gen
         for ConferenceTileViewModel + ConferenceGalleryViewModel. -->
  </ItemGroup>
</Project>
```

**Dependencies that DO NOT bleed in:**
- AForge (Teacher cam capture stays in Teacher)
- NAudio (audio pipelines stay in Teacher/Student.Agent)
- MaterialDesignThemes (Teacher/Student already reference; not needed
  by the Conference views directly — they use DynamicResource keys
  populated at runtime by BrandingService)
- Networking — views don't talk to TCP directly

**ContextDataContext story:**
- `ConferenceToolbar`, `ConferenceSidebar`, `ConferenceGalleryView` bind
  to commands like `ToggleMicCommand`, `SendReactionCommand`,
  `EndConferenceCommand`. Today these live on `Teacher.MainViewModel`.
- Solution: introduce `IConferenceShellViewModel` in Shared.Wpf with a
  superset of commands. Teacher's `MainViewModel` implements it. Student
  builds a `StudentConferenceShellViewModel` that implements the same
  interface but routes commands through its own IPC client.
- 16-D's role model layers on top: `Role.CanX` properties bound by
  toolbar/sidebar elements to gate UI visibility.

**Pros:**
- Clean ship-separate binaries.
- Zero file duplication → no drift risk.
- Foundation for 14-C Tier 2 (student↔student screen share) +
  16-C (peer cam).
- 15-C "smart deviation" debt cleared.

**Cons:**
- New project = solution-file edit, ~30 min plumbing.
- All XAML namespace refs (`xmlns:local="clr-namespace:..."`) must
  re-target the new assembly.
- `App.xaml.cs` resource pump (BrandingService / Localization) must
  also pump into Shared.Wpf's `Application.Current.Resources` (works
  automatically because the views use DynamicResource — they consume
  whatever `App.Current.Resources` exposes).

#### Option B — Duplicate files into Student.Agent

Copy the 4 XAML files into `Student.Agent/Views/Conference/`.

**Pros:** no new project. **Cons:** 2× maintenance, guaranteed drift
the moment Teacher tweaks a tile.

**Verdict:** rejected. The session record explicitly warns: "couple
ship-separate binaries… don't do it." Duplication is worse than coupling
because at least coupling has a compiler check.

#### Option C — Inline minimal student gallery (status quo + a bit more)

Keep `ConferenceGalleryWindow` simple; add only the bits needed for
basic peer cam rendering (a per-peer Image list). Skip the polished
toolbar / sidebar / reactions on student side.

**Pros:** smallest delta. **Cons:** breaks customer ask for symmetric
UX. Punts the Shared.Wpf debt and forces it again later.

**Verdict:** rejected because the customer ask is explicit about
parity.

### Recommendation: Option A.

---

## 5. Host vs Participant Role Model

### Interface

Lives in Shared.Wpf so both Teacher and Student bind against it.

```csharp
public interface IConferenceRole
{
    // Self-controls — both Host and Participant have these.
    bool CanToggleOwnMic    { get; }
    bool CanToggleOwnCam    { get; }
    bool CanSendChat        { get; }
    bool CanSendReaction    { get; }
    bool CanRaiseOwnHand    { get; }
    bool CanLeave           { get; }

    // Admin-only — Host has these; Participant does not.
    bool CanMuteOthers      { get; }
    bool CanEndForAll       { get; }
    bool CanRecognizeHand   { get; }
    bool CanPinForAll       { get; }   // distinct from local pin
    bool CanForceCamToggle  { get; }   // v2 / 16-E polish
    bool CanManageParticipants { get; }
}

public sealed class HostRole : IConferenceRole
{
    public bool CanToggleOwnMic     => true;
    public bool CanToggleOwnCam     => true;
    public bool CanSendChat         => true;
    public bool CanSendReaction     => true;
    public bool CanRaiseOwnHand     => true;
    public bool CanLeave            => true;
    public bool CanMuteOthers       => true;
    public bool CanEndForAll        => true;
    public bool CanRecognizeHand    => true;
    public bool CanPinForAll        => true;
    public bool CanForceCamToggle   => true;
    public bool CanManageParticipants => true;
}

public sealed class ParticipantRole : IConferenceRole
{
    public bool CanToggleOwnMic     => true;
    public bool CanToggleOwnCam     => true;
    public bool CanSendChat         => true;
    public bool CanSendReaction     => true;
    public bool CanRaiseOwnHand     => true;
    public bool CanLeave            => true;
    public bool CanMuteOthers       => false;
    public bool CanEndForAll        => false;
    public bool CanRecognizeHand    => false;
    public bool CanPinForAll        => false;
    public bool CanForceCamToggle   => false;
    public bool CanManageParticipants => false;
}
```

### Wiring

`IConferenceShellViewModel.Role` property exposes the active role.
Teacher's shell VM always sets `new HostRole()`; Student's sets
`new ParticipantRole()`. No co-host pattern for v1 (15-A architecture
doc deferred to v2 explicitly).

### UI binding pattern

Hidden, not disabled, for admin controls — disabled controls confuse
students into thinking they could be enabled.

```xml
<!-- End-for-all button — host only. -->
<Button Command="{Binding EndConferenceCommand}"
        Visibility="{Binding Role.CanEndForAll,
                     Converter={StaticResource BoolToVisibility}}"/>

<!-- Recognize button on hand-queue row — host only. -->
<Button Command="{Binding DataContext.RecognizeHandCommand, ...}"
        CommandParameter="{Binding}"
        Visibility="{Binding DataContext.Role.CanRecognizeHand,
                     RelativeSource={RelativeSource AncestorType=UserControl},
                     Converter={StaticResource BoolToVisibility}}"/>
```

For student-side toolbar, the End button (📞) becomes "Leave" (🚪)
labeled — different verb, same command name on the interface if we
follow Meet's convention.

### Capability matrix (for 16-D test rig)

| Element | Host visible? | Participant visible? |
|---|---|---|
| 🎙 mic toggle | ✅ | ✅ |
| 📷 cam toggle | ✅ | ✅ |
| 🖥 share screen | ✅ | ✅ |
| 💬 chat sidebar | ✅ | ✅ |
| ✋ raise hand (self) | ✅ | ✅ |
| ⋮ reactions | ✅ | ✅ |
| 📞 End for All / 🚪 Leave | End | Leave |
| Recognize button on hand-queue row | ✅ | ❌ |
| Mute Others button | ✅ | ❌ (v2 — not in 16-D) |
| Force-cam button | ❌ (v2) | ❌ |
| Pin for All (vs local pin) | ✅ | ❌ |

---

## 6. Peer Cam Routing Design

See [`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md) for the full design. Summary:

- Existing `0x0460 CameraStart` / `0x0461 CameraFrame` / `0x0462
  CameraStop` are unidirectional (teacher→student) and mode-bound to
  Classroom (Phase 9.5).
- **Recommendation: new wire codes** `0x0680 ConferenceCameraStart` /
  `0x0681 ConferenceCameraFrame` / `0x0682 ConferenceCameraStop` for
  Conference mode. Rationale: keeps Classroom/Conference dispatch arms
  cleanly separated, no risk of behaviour collision when a 9.5 broadcast
  overlaps a Conference frame.
- Routing: each participant emits with own `Envelope.SenderId`; teacher
  acts as relay (star topology) and fans out to all in-Conference peers
  except the sender (self-loopback filter, same pattern as 13-D voice).
- Bandwidth math (148 KB/s per stream from CamSpike actual):
  - 5 students: ~24 Mbps egress
  - 10 students: ~107 Mbps
  - 20 students: ~450 Mbps (gigabit edge)
  - 30+ students: needs SFU → defers to 15-F (selective forwarding +
    thumbnails)

Mode-aware emission: a student's `StudentCameraBroadcaster` (NEW)
listens to `IsInConference` and emits to 0x0680-0x0682 when in
Conference, 0x0460-0x0462 otherwise. Both modes are mutually exclusive
per the architecture-doc § 3 mode-exclusivity table, so no dual-emit
case to worry about.

---

## 7. Risks Register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | H1 isn't the gallery UI bug; deeper layout issue | Med | High | 16-B step 5 starts with diagnostic logging; if H1 fix doesn't resolve, fall back to instrumenting `RenderSize` per element |
| 2 | Shared.Wpf pulls unintended deps (Theming) into Student.Agent | Med | Med | Keep `.csproj` minimal (only `ClassroomCtrl.Shared` + `CommunityToolkit.Mvvm`); verify Student.Agent build size after extract |
| 3 | Resource pump (`App.Current.Resources`) gets out of sync between Teacher and Student for DynamicResource keys | Low | Med | Shared.Wpf views use only existing keys (Surface.*, Border.*, Text.*) populated by `BrandingService` in both apps |
| 4 | Mode-aware cam emission collides if student is somehow in both modes | Very Low | High | Defensive guard in `StudentCameraBroadcaster.OnFrame`: assert `_modeFlag != Both`; log + drop frame on violation |
| 5 | Force-mute by host via new wire vs reusing 13-D `MicMuteRequest` | Med | Low | Decision flag in 16-D: reuse 13-D 0x0641 (`MicMuteRequestMessage`) — already targeted, reliable, with reason field |
| 6 | Force-cam by host — needs new wire OR extend 14-B WebcamMode | Med | Med | Defer to 16-E polish; not in 16-D primary scope |
| 7 | Peer cam tile flicker when student leaves Conference mid-stream | Med | Low | Tile-removal animation; 5-frame grace window before drop (mirrors 13-B GroupSnapshot leave handling) |
| 8 | Pin-for-all (spotlight) wire — separate from local pin | Low | Low | 16-D step 4 — add `0x0673`-style `SpotlightSet` envelope OR carry via existing pin command with broadcast flag |
| 9 | Bandwidth at 30+ class without SFU | Low (school MVP) | High at scale | Document 15-F SFU trigger criteria; flag for customer-site validation if class size grows |
| 10 | Self-loopback for new 0x0681 frames — duplicates 13-D filter logic | Low | Low | Reuse exact pattern: `if (env.SenderId == _myEndpointId) return;` at receive arm |
| 11 | Project-file edit (`.sln`) introduces conflict if dev has uncommitted shell work | Low | Med | Verify clean tree before 16-B step 1; commit `.sln` change as the first atomic step |
| 12 | XAML namespace updates miss a binding → silent rendering miss | Med | Low | 16-B step 7 — full build + visual smoke test before declaring step complete |
| 13 | 15-C deviation flag (single-tile student window) doc-rot — old docs still reference it | Low | Low | 16-A docs supersede; future readers see 16-A timeline; old refs in 15-A/B/C/D/E doc-set acknowledged in retrospect-only context |

---

## 8. Phase ordering decision

```
16-B  Shared.Wpf extraction + 15-C UI bug fix     ~2-3 hr   foundation
16-C  Peer cam routing                             ~1-2 hr   feature parity
16-D  Host vs Participant role + UI permissions    ~1 hr     ship-ready
16-E  Polish (host badge, empty states, etc.)     ~30 min   optional
```

Total: ~5-7 hr. Dependencies:
```
16-B  ┐
      ├─→ 16-C  ┐
                ├─→ 16-D  ←  optional 16-E
```

16-B must land first because everything else assumes Shared.Wpf exists
and 15-C gallery renders. 16-C and 16-D can ship in either order after
16-B; recommended order is 16-C then 16-D so the gallery has real peer
cam frames to test the role-gated UI against.

After 16-D, Conference Mode is **shippable in customer site** for class
sizes up to ~10 students (bandwidth headroom on gigabit). Above 20
needs 15-F (SFU); customer-site bandwidth verification gates that
trigger.

---

## 9. What 16-A explicitly does NOT include

- Any code changes (per primer constraint).
- Wire codepoint additions to `Messages.cs` / `MessageType.cs` (sketched
  only).
- New project file in the solution (`Shared.Wpf.csproj` design only).
- Role interface in code (design only; lands in 16-B step 1 alongside
  the Shared.Wpf project skeleton).
- Diagnostic logging additions to existing files.

---

## 10. After Phase 16-A

1. Dev reviews 4-5 docs (this + 3 tier siblings + optional plan).
2. Decides:
   - Proceed to 16-B (~2-3 hr) — recommended.
   - Or revise scope if Shared.Wpf reveals heavy dependency issues
     (worst case: pivot to Option B duplicate, with documented drift
     budget).
3. After 16-B, dev validates UI bug fix + Student gallery renders.
4. Then 16-C peer cam, then 16-D roles.
