# Session Summary — 2026-07-12

macOS/Avalonia port of NTY ClassroomCtrl. This session took the port from nothing to a
**method fully validated end-to-end on real hardware** — **15 milestones**, capped by a
**LIVE 4/4-PASS interop test against the shipped Windows Teacher v1.2**. It: extracted the
wire protocol, **proved cross-platform interop on real Windows hardware (byte-perfect)**,
built the **complete Conference Mode UI** (Tile + Toolbar + Sidebar, composed), ported the
**Classroom Mode foundation** (StudentCard + ContextMenu, a reusable **design system**, and
**four dialogs** incl. the first list + first Student-side views), and made the Sandbox a
**functional wire student** (`WireClient` service) that **connects to, appears in, and
reflects commands from a real Windows Teacher**. Produced reusable tooling (**20-section**
cheat sheet, full theme, headless-capture harness, MockTeacher). Phases 25.5→26.0 were
**execution, not discovery** — and that thesis is now **proven at scale**.

**Milestone 15 — LIVE-CONFIRMED (2026-07-12):** Windows Teacher `172.20.10.7:7777` ↔ Mac
Sandbox `172.20.10.2`. **4/4 PASS** — Lock ✅ · Apply Policy ✅ · Chat ✅ · Raise Hand ✅
(bidirectional). Screen view **correctly deferred** (Phase 27+). Byte-perfect against
production code. See `docs/PHASE-26.0-LIVE-CONFIRMATION.md` + screenshot
`docs/live-test-2026-07-12/Screenshot 2569-07-12 at 23.45.42.png` (commit `efb3c5e`).

**Repo:** `~/Dev/nty-classroom-avalonia/` (separate from the shipped Windows repo
`~/Dev/nty-classroom-macos/`, which was **never modified** — verified clean after
every phase). Branch: `avalonia-experiment` (remote `origin`:
github.com/forcesumetee/nty-classroom-ctrl).

---

## Phases completed (24.1 → 26.0) — 15 milestones

| Phase | Deliverable | Outcome |
|---|---|---|
| **24.1** | Extract wire protocol as `ClassroomCtrl.Shared.Wire` | Carved the pure protocol out of the Windows-locked `Shared` assembly. net10.0, MessagePack **2.5.187 pinned**. **T1–T26 all pass on macOS.** |
| **24.2** | Cross-platform interop client (`tools/CrossPlatformInteropTest`) | macOS console app: UDP beacon decode + TCP Hello/Ping→Pong handshake. Loopback-proven, then **✅ PROVEN on REAL Windows Teacher hardware today** — beacon decoded (ChannelId 1234, 172.20.10.7:7777), Hello(215B)+Ping(103B)→Pong(102B), byte-identical, <1 s round-trip. |
| **24.3** | First view port — `ConferenceTile` (Avalonia Sandbox app) | Renders on macOS; all state paths verified. Found + codified the "local value beats style setter" trap. |
| **25.0-A** | `docs/WPF-TO-AVALONIA-CHEATSHEET.md` | Reusable porting reference grounded in the real port; unverified claims tagged. |
| **25.0-B** | Full `ConferenceDarkTheme` port (36 keys) + theme showcase | All 36 keys, names preserved verbatim; keyed `Style`→`ControlTheme` pattern learned + added to cheat sheet §3e. |
| **25.1-A** | ConferenceToolbar — layout + active states | 7 buttons via the ControlThemes; per-button `DataTrigger`→`.active`/`.pending` classes; mic-green / End-red verified. |
| **25.1-B** | Reaction picker Flyout | WPF `Popup` → Avalonia `Button.Flyout`; 5 emoji reactions → `SendReactionCommand`. |
| **25.1-C** | Reaction float animation on ConferenceTile | **Fulfils the Phase 24.3 `TODO`** — WPF Storyboard → Avalonia `Animation`. |
| **25.1-D** | Conference bottom-bar demo + docs | Toolbar + tiles; picked reaction floats over a tile — full chain proven mid-float (`docs/phase-25.1-bottombar.png`). |
| **25.2** | Promote headless-Skia harness → `tools/HeadlessCapture/` | Reusable off-screen screenshot tool (no display/permission). Hybrid scenario-registry (`theme`/`tile`/`bottombar`, self-documenting metadata) + generic `--view-type` reflection + `--list`. Verified by re-capture; baselines preserved, differences explained. |
| **25.3-A/B** | ConferenceSidebar — shell, tabs, chat + participants, input, raised-hands, role-gated Mute/Recognize | Manual tabs (source hand-rolls, not TabControl) → classes + `IsVisible`; `SidebarTabPill` = 3rd ControlTheme; reuses the **real ported `ChatMessage`** model; inert attachment card. |
| **25.3-C** | Full Conference composition + `sidebar` scenario | **Milestone 9** — tile grid + toolbar (bottom) + sidebar (right) in one window (`docs/phase-25.3-conference-full.png`). |
| **25.3-D** | Docs — cheat sheet §12 (manual tabs) + §13 (advanced binding scopes: `$parent` = FindAncestor + compiled-binding cast) | `PHASE-25.3-FINDINGS.md`. |
| **25.4-A/B** | StudentCard — layout/badges/selection + ContextMenu (17 items + separators + static nested submenu) | `HexToBrushConverter` ported; Classroom Material theme keys mapped to `Conf*`. |
| **25.4-C** | **ContextMenu command routing — the crux, verified empirically** | `$parent` resolves **null** across the popup boundary; **fix = `Host` back-reference on the item** (`{Binding Host.X}`). Flat + nested-submenu routing proven (correct param). |
| **25.4-D** | `studentcard` scenario (menu open) + cheat sheet §11/§13 + findings | **Milestone 10** — hardest flagged pattern cracked (`docs/phase-25.4-studentcard-menu.png`). |
| **25.4-E** | Dynamic `ItemsSource` "Assign to room" submenu + routing | Dynamic nested menu via `MenuItem.ItemsSource` + `ItemContainerTheme`; per-item `Host` routing. Closed the ContextMenu story. |
| **25.5-B/C/D** | Classroom **light theme** (`ClassroomLightTheme.axaml`, 36 keys) + icon proof + `classroom` scenario + cheat sheet §14/§15 | **Milestone 12** — both palettes (Conference dark + Classroom light) coexist. Icon strategy = **NONE NEEDED** (emoji + Unicode render natively on macOS). `docs/phase-25.5-classroom-theme.png`. |
| **25.6-B/C/D** | Classroom **design system** (`ClassroomControls.axaml` — Button ControlThemes + Text classes) + **ApplyPolicyDialog** + cheat sheet §16 | **Milestone 13** — design system unblocks all Teacher dialogs; flagship dialog ported mechanically. New idiom: `ThemeVariantScope=Light` for light views in a dark-default app (§16). `docs/phase-25.6-applypolicy.png`. |
| **25.7-A/B/C/D** | **Three more dialogs** — ClassRosterManager (list), CameraSelectorDialog (form), TeacherIPDialog (input+validation, first **Student-side** view) + cheat sheet §17 | **Milestone 14** — design system proven at scale across 3 dialog shapes. `ListView`/`GridView` → **templated `ListBox`** decision codified (§17). 3 baselines. |
| **26.0-A** | `WireClient` service + connection UI | Promotes the 24.2 transport into a persistent student: Hello + 5 s Ping heartbeat + read loop + reconnect (2s→×2→30s), UI-agnostic. `ConnectionViewModel` marshals to the UI thread. `docs/phase-26.0-connection.png`. |
| **26.0-B** | **MockTeacher** (`tools/MockTeacher`) + `--selftest` | Local Teacher stand-in; `--selftest` drives the REAL `WireClient` through the full loop → **PASS** (exit 0). De-risked the live test; reusable regression harness. |
| **26.0-C** | Command reception + debug display | `Dispatch` decodes inbound envelopes → decoded traffic log + self-tile reflection (lock/policy/chat/hand); capture-class commands noted **deferred (Phase 27+)**. Bonus S→T Raise Hand. |
| **26.0-D** | Cheat sheet §19 (background services + Dispatcher) + findings + live-test staging | `docs/LIVE-TEST-26.0.md` one-shot procedure. Cheat sheet 18 → **20 sections**. |
| **26.0 LIVE** | **Real Windows Teacher interop test** | **Milestone 15 — LIVE-CONFIRMED, 4/4 PASS** (Lock · Policy · Chat · Raise Hand bidirectional; screen view correctly deferred). Byte-perfect vs production code. `docs/PHASE-26.0-LIVE-CONFIRMATION.md`, screenshot, commit `efb3c5e`. |

## Commit list (this session) — 39 commits + this closeout

```
efb3c5e  Phase 26.0 LIVE: real Windows Teacher interop screenshots — all 4 tests PASS
4f2c033  Phase 26.0-D: cheat sheet §19 + findings + live-test staging + push
269cf69  Phase 26.0-C: command reception + debug display
8153c75  Phase 26.0-B: MockTeacher (local Teacher stand-in + full-loop self-test)
31e6da0  Phase 26.0-A: WireClient service + connection flow
45536c6  Phase 25.7-D: cheat sheet §17 (list rendering) + findings + push
a843c48  Phase 25.7-C: port TeacherIPDialog (first Student-side view + validation)
010d92b  Phase 25.7-B: port CameraSelectorDialog (form + multi-combo)
b390b79  Phase 25.7-A: port ClassRosterManager (ListBox+Grid list view)
19d34d5  Phase 25.6-D: cheat sheet §16 (ThemeVariantScope) + findings + push
bc645c6  Phase 25.6-C: port ApplyPolicyDialog (flagship Classroom dialog)
5526524  Phase 25.6-B: Classroom design system — Button ControlThemes + Text classes
9842ad8  Phase 25.5-D: cheat sheet §14/§15 + PHASE-25.5-FINDINGS + push
d7a8d99  Phase 25.5-C: Classroom light-theme showcase + icon proof + 'classroom' scenario
6f6e666  Phase 25.5-B: port Classroom Colors.Light → ClassroomLightTheme.axaml (36 keys)
d372a72  Phase 25.4-E (3+4): submenu-open baseline + cheat sheet §11/§13 + findings
324859f  Phase 25.4-E (1+2): dynamic ItemsSource submenu + routing + $parent finding
925edb4  Session closeout: summary through Phase 25.4 (10 milestones)
1039d7b  Phase 25.4-D: studentcard scenario + cheat sheet §11/§13 + findings
e41b552  Phase 25.4-C: command routing verified — $parent fails in popup, Host works
be8bab6  Phase 25.4-B: StudentCard ContextMenu → Avalonia ContextMenu
e15457f  Phase 25.4-A: StudentCard port — layout, badges, selection ring
883a9c9  Phase 25.3-D: docs — cheat sheet §12/§13 + PHASE-25.3-FINDINGS
dbb2ff1  Phase 25.3-C: full Conference composition + 'sidebar' scenario
bdb2f87  Phase 25.3-B: sidebar chat input, raised-hands, Mute/Recognize (role-gated)
af34724  Phase 25.3-A: ConferenceSidebar port — shell, tabs, chat + participants
8ec3c17  Phase 25.2 closeout: session summary update
05dec39  Phase 25.2: Promote headless-Skia harness to tools/HeadlessCapture
9054b39  Phase 25.1 closeout: session summary update + push
3acfe2c  Phase 25.1-D: Conference bottom-bar demo + docs
6f9abf9  Phase 25.1-C: reaction float animation on ConferenceTile
798771d  Phase 25.1-B: reaction picker Flyout on the More button
5bcad62  Phase 25.1-A: ConferenceToolbar port — layout + active states
a241642  Phase 25.0-B closeout: session summary + push
4d9c52b  Phase 25.0-B: Full ConferenceDarkTheme port (36 keys)
ba414c4  Phase 25.0-A: WPF->Avalonia cheat sheet
4c27199  Phase 24.3: First view port — ConferenceTile
5d79569  Phase 24.2: Cross-platform interop test (macOS -> Windows Teacher)
97e1051  Phase 24.1: Extract wire protocol as ClassroomCtrl.Shared.Wire
```

## Empirical findings (the durable value)

### Timeline data — the XAML port is NOT the bottleneck
Grounded in the real ConferenceTile port + a 50-view inventory (Shared.Wpf 6,
Teacher 28, Student.Agent 16):

| Tier | Count | Per-view | Subtotal |
|---|---|---|---|
| Simple (≤80 lines) | ~20 | 0.5–1 h | ~15 h |
| Medium (ConferenceTile-class) | ~22 | 1.5–3 h | ~50 h |
| Complex (MainWindow 1251, Sidebar 558, StudentCard ContextMenu…) | ~8 | 4–10 h | ~50 h |

- **View XAML total ≈ 115 h (~3 focused weeks).**
- The theoretical **6–8 month** estimate is driven almost entirely by the
  **platform-API rewrites** (ScreenCaptureKit, VideoToolbox, AVFoundation, policy/
  lock enforcement, tray, global hooks) — **not** the UI. Recommendation: keep the
  6–8 month range but shift its risk weighting onto native-API subsystems.

### Syntax gap (WPF → Avalonia) — see the cheat sheet for full detail
- **No triggers** → conditional style classes (`Classes.foo="{Binding}"`) + selectors.
- **No `Visibility`** → `bool IsVisible`; most converters evaporate (built-ins
  `ObjectConverters`/`StringConverters` cover null/empty checks).
- `DependencyProperty` → `StyledProperty`; keyed `Style`+`ControlTemplate` →
  `ControlTheme` (`Theme="{StaticResource}"`, `:pointerover /template/` selectors).
- **Trap:** a locally-set property outranks a style setter (same as WPF) — base
  values a class toggles must live in a style, not inline. (Cost the one real bug.)
- ViewModels port with **zero logic changes** (CommunityToolkit.Mvvm unchanged);
  only WPF *types* swap (e.g. `BitmapSource`→`Bitmap`).
- Adopt `x:DataType` compiled bindings from day one (binding typos → build errors).
- Keyed `Style`+`ControlTemplate` → `ControlTheme` (`Theme="{StaticResource}"`,
  `:pointerover /template/` pseudo-class selectors). WPF `Popup` → `Button.Flyout`.

### Animation gotchas (Phase 25.1 — cheat sheet §10) — worth hours per person
WPF `Storyboard` → Avalonia `Animation` (`KeyFrame`/`Cue`/`Setter`/`RunAsync`). Two
code-created-animation traps cost real debug time and are now documented:
1. **`Animation.RunAsync` targets a `Visual`** — running it on a bare
   `TranslateTransform` throws `InvalidCastException`.
2. **`RenderTransform`/`translateY` keyframes are XAML-only from code** — the
   `TransformOperationsAnimator` is `internal` and only auto-registers via XAML;
   `Animation.Animators` isn't public. **Fix:** animate a default-registered
   property (`Margin`/`ThicknessAnimator`, or a `double` like `Canvas.Top`). The
   reaction float uses `Margin.Top` `+20→-80` on a centered glyph.

### ContextMenu routing — the hardest pattern (Phase 25.4, §11/§13) — the 4 answers
1. **`$parent[…].((vm:Host)DataContext).Command` from a ContextMenu item → NO.** It
   resolves **null** — `$parent` ancestor-walk stops at the popup root (separate visual
   tree). **Fix: `Host` back-reference on the item** (`{Binding Host.X}`,
   `CommandParameter={Binding}`), since the ContextMenu inherits the target's DataContext.
   Verified firing (flat + nested submenu, correct param). WPF `PlacementTarget.Tag.X` → `Host.X`.
2. **Cascading submenu → matches WPF** (nested `<MenuItem>`, ▸ chevron, hover-open, fires).
3. **Segoe MDL2 → N/A here** (text headers; card emoji render via Apple Color Emoji);
   icon system deferred to a dedicated phase.
4. **Right-click gesture → simpler than WPF** — Avalonia opens ContextMenu/ContextFlyout
   automatically; no code-behind Tag-stash.

**Bonus:** Avalonia **`ContextMenu` popups DO render into headless `CaptureRenderedFrame`**
(open via `contextMenu.Open(target)` first) — unlike a `Button.Flyout`. Extends visual-
regression coverage to menu-open baselines.

### Cheat sheet growth (living doc)
Grew to **20 numbered sections** (deepened, not just extended): §3e keyed-Style→ControlTheme
(25.0-B) · §10 Animations + §11 Popups/Flyouts (25.1) · §12 Manual-tabs-vs-TabControl +
§13 Advanced binding scopes (25.3) · §11/§13 ContextMenu + popup-boundary routing + dynamic
`ItemsSource` submenu (25.4) · §14 icon-strategy=none-needed + §15 multiple theme dictionaries
coexisting (25.5) · §16 `ThemeVariantScope` for light views in a dark app (25.6) · §17
`ListView`/`GridView` → templated `ListBox` + when-to-`DataGrid` (25.7) · §19 background
services + `Dispatcher` marshalling / `CancellationTokenSource` lifetime (26.0); references
at §20. Unverified rows still explicitly tagged. Still uncovered: complex ControlTemplate
re-authoring, DynamicResource theme-swap.

### Tools built (reusable)
- **T1–T26 wire compat harness** (`tools/EnvelopeWireCompatTest`) — runs on macOS.
- **Cross-platform interop client** (`tools/CrossPlatformInteropTest`).
- **`tools/HeadlessCapture`** (Phase 25.2) — reusable off-screen screenshot tool, no
  display / screen-recording permission. Scenario registry + generic `--view-type`
  reflection + `--list`. **11 scenarios** now registered (`theme`/`tile`/`bottombar`/
  `sidebar`/`studentcard`/`classroom`/`applypolicy`/`roster`/`camera`/`teacherip`/
  `connection`). Add one per future port.
- **`tools/MockTeacher`** (Phase 26.0) — local Windows-Teacher stand-in; interactive
  command mode (run alongside the Sandbox GUI) + `--selftest` that drives the real
  `WireClient` through the full loop (exit 0=PASS). De-risks live tests; regression harness.
- **`WireClient` service** (Phase 26.0) — UI-agnostic student-side transport (Hello +
  5 s heartbeat + read loop + reconnect); byte-confirmed against the real Windows Teacher.
- **WPF→Avalonia cheat sheet** — living document (**20 sections**).
- **Both theme palettes** — ConferenceDarkTheme (36 keys) + ClassroomLightTheme (36 keys),
  coexisting — plus the **Classroom design system** (`ClassroomControls.axaml`).
- **Complete Conference Mode UI** (Tile + Toolbar + Sidebar, composed) + **Classroom
  StudentCard + ContextMenu** + **four Classroom dialogs** (ApplyPolicy, Roster, Camera,
  TeacherIP) + the **Connection panel** (live wire client).

### Constraints (verified honored)
- MessagePack pinned exactly `2.5.187` for byte-compat (NU1902/NU1903 advisories
  are expected and must NOT be "fixed" by upgrading — see memory note).
- Shipped Windows repo untouched throughout.

## Status of prior pending items

- [x] **Real Windows-Teacher interop verification** — ✅ byte-perfect on real hardware
  (Milestone 15 LIVE, 4/4 PASS).
- [x] **Conference Mode UI** (Toolbar/Sidebar/full composition) · **StudentCard ContextMenu**
  (+ dynamic submenu) — ✅ all done.
- [x] **Classroom Mode foundation** — light theme + design system + 4 dialogs — ✅ done.
- [x] **Functional wire student** (connect/appear/receive/reflect) — ✅ done + LIVE-confirmed.
- [ ] **v1.2.1 bulk lock fix** — Windows product, **separate track** (next-session Option 1).
- [ ] **Phase 27 native APIs** (screen capture / video / camera / audio / enforcement) —
  the deferred boundary; **not started** (next-session Option 2).

## Next-session preparation notes

- Open the cheat sheet (`docs/WPF-TO-AVALONIA-CHEATSHEET.md`) first — the porting playbook
  (**20 sections**; §11/§13 ContextMenu+routing, §16 ThemeVariantScope, §17 lists, §19
  background services + Dispatcher).
- Sandbox app: `dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox` — MainWindow is a
  **12-tab** TabControl: Theme tokens · ConferenceTile · Bottom bar · Sidebar · Conference
  Full · Student Grid · Classroom · Apply Policy · Roster · Camera · Teacher IP · Connection.
- Build: `dotnet build ClassroomCtrl.Avalonia.slnx`. Screenshots:
  `dotnet run --project tools/HeadlessCapture -- --list` then `--scenario <name>`.
  Wire self-test: `dotnet run --project tools/MockTeacher -- --selftest`.
- **Method is fully validated end-to-end on real hardware.** Idioms to remember:
  base-values-in-a-style (§6), `Host` back-reference for popup/menu commands (§13),
  `ThemeVariantScope=Light` for light dialogs (§16), and service=async+events / VM=Dispatcher
  (§19). Live-test procedure: `docs/LIVE-TEST-26.0.md`.

---

## Next session — recommended priority order

**1 — v1.2.1 Windows bulk-lock fix** (~2–3 h) · *customer priority, external track*
The bulk-lock 18/50 issue in the shipped product. **Separate from the macOS port** — lives
in the shipped Windows repo (`~/Dev/nty-classroom-macos/`), NOT this branch. Schedule first
by customer urgency; it does not touch the Avalonia work.

**2 — Phase 27 native APIs kick-off** (~3–5 h per subsystem) · *the real remaining risk*
The deferred boundary is now the critical path. Start with **screen capture (ScreenCaptureKit)**
— the Teacher's `StudentStreamStart`/`RequestScreenshot` already arrive (proven LIVE, currently
noted deferred); wiring real capture turns the Mac into a viewable student. Then VideoToolbox
(encode), AVFoundation (camera/audio), NSWorkspace/overlay (lock/policy enforcement). Per the
timeline analysis, these subsystems — not the UI — carry the 6–8-month weight.

**3 — Phase 25.8+ progressive UI ports** (~3–5 h each) · *execution, low risk*
Continue porting Teacher/Student views on the proven design system (heavier dialogs:
Branding ~232 lines, GroupManager ~200 lines; or the main grid shell). Fully mechanical now.

**4 — Runtime light/dark theme swap** (~1–2 h)
Wire `RequestedThemeVariant` switching so Conference (dark) and Classroom (light) coexist at
runtime rather than per-view `ThemeVariantScope`. Polish; unblocks a unified shell.

**5 — Business: customer conversation with LIVE screenshots**
The Milestone-15 evidence (`docs/PHASE-26.0-LIVE-CONFIRMATION.md` + screenshot) is
demo-ready: a Mac student live in the shipped Windows Teacher. Use for the customer/portfolio.

**Recommendation:** #1 if the customer is waiting; otherwise **#2 (ScreenCaptureKit)** is the
highest-leverage next step — it attacks the actual remaining risk and builds directly on the
LIVE-confirmed wire foundation.

---

## Team handoff status

A new team member picks up a **complete, de-risked foundation**:
- **This summary** + `docs/PHASE-26.0-LIVE-CONFIRMATION.md` (LIVE evidence).
- **20-section cheat sheet** of battle-tested WPF→Avalonia patterns.
- **15 milestones** documented; **Foundation phase = COMPLETE**, **Execution phase =
  PROVEN at scale** (4 dialogs + full Conference UI + live wire client).
- **Shipped Windows repo never touched** (verified clean after every phase).
- Ready for **progressive Phase 27+ work** (native APIs) on a byte-confirmed wire base.
