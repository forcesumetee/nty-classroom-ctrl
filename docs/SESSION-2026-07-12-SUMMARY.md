# Session Summary — 2026-07-12

macOS/Avalonia port of NTY ClassroomCtrl. This session took the port from nothing to
a **validated method across every major WPF pattern** — 10 milestones. It: extracted
the wire protocol, **proved cross-platform interop on real Windows hardware
(byte-perfect)**, built the **complete Conference Mode UI** (Tile + Toolbar + Sidebar,
composed), laid the **Classroom Mode foundation** (StudentCard + its ContextMenu), and
produced reusable tooling (14-section cheat sheet, full theme, headless-capture tool).
Phase 25.5+ is now **execution, not discovery**.

**Repo:** `~/Dev/nty-classroom-avalonia/` (separate from the shipped Windows repo
`~/Dev/nty-classroom-macos/`, which was **never modified** — verified clean after
every phase). Branch: `avalonia-experiment` (remote `origin`:
github.com/forcesumetee/nty-classroom-ctrl).

---

## Phases completed (24.1 → 25.4) — 10 milestones

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

## Commit list (this session) — 21 commits + this closeout

```
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
Grew to **14 numbered sections** (deepened, not just extended): §3e keyed-Style→ControlTheme
(25.0-B) · §10 Animations + §11 Popups/Flyouts (25.1) · §12 Manual-tabs-vs-TabControl +
§13 Advanced binding scopes (25.3) · §11/§13 ContextMenu + popup-boundary routing (25.4);
references at §14. Unverified rows still explicitly tagged.

### Tools built (reusable)
- **T1–T26 wire compat harness** (`tools/EnvelopeWireCompatTest`) — runs on macOS.
- **Cross-platform interop client** (`tools/CrossPlatformInteropTest`).
- **`tools/HeadlessCapture`** (Phase 25.2) — reusable off-screen screenshot tool, no
  display / screen-recording permission. Scenario registry (`theme`/`tile`/`bottombar`,
  self-documenting) + generic `--view-type` reflection + `--list`. Add a scenario per
  future port. (Promoted from the scratchpad `CaptureTile` harness.)
- **WPF→Avalonia cheat sheet** — living document (14 sections).
- **Full ConferenceDarkTheme** + theme showcase baseline.
- **Complete Conference Mode UI** (Tile + Toolbar + Sidebar, composed) + **Classroom
  StudentCard + ContextMenu** — 5 HeadlessCapture scenarios
  (`theme`/`tile`/`bottombar`/`sidebar`/`studentcard`).

### Constraints (verified honored)
- MessagePack pinned exactly `2.5.187` for byte-compat (NU1902/NU1903 advisories
  are expected and must NOT be "fixed" by upgrading — see memory note).
- Shipped Windows repo untouched throughout.

## Status of prior pending items

- [x] **Real Windows-Teacher interop verification** — ✅ byte-perfect on real hardware.
- [x] **Phase 25.1 ConferenceToolbar** · **25.3 ConferenceSidebar** · **25.4 StudentCard
  ContextMenu** — ✅ all done.
- [ ] **v1.2.1 bulk lock fix** — Windows product, **separate track** (see Option 4).

## Next-session preparation notes

- Open the cheat sheet (`docs/WPF-TO-AVALONIA-CHEATSHEET.md`) first — the porting playbook
  (14 sections; §11/§13 cover ContextMenu + popup-boundary routing).
- Sandbox app: `dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox` — MainWindow is
  a TabControl: Theme tokens · ConferenceTile · Bottom bar · Sidebar · Conference Full ·
  Student Grid.
- Build: `dotnet build ClassroomCtrl.Avalonia.slnx`. Screenshots:
  `dotnet run --project tools/HeadlessCapture -- --list` then `--scenario <name>`.
- **Method is validated** — 25.5+ is execution. Two idioms to remember: base-values-in-a-
  style (§6) and `Host` back-reference for popup/menu commands (§13).

---

## Next session — pick ONE (not started)

**Option 1 — Icon/font system** (~2–4 h)
Real blocker for icon-heavy Teacher views (Segoe MDL2 → cross-platform font or vector
set). Requires research + a design decision (icon font vs SVG resources vs per-glyph
mapping). High leverage: unblocks most remaining Teacher UI. Pairs with porting the
Classroom **Material theme** keys (`Surface.*`, `Text.*`, `Border.*`).

**Option 2 — Dynamic `ItemsSource` submenu** (~30–60 min)
Finish the deferred StudentCard "Assign to Room" submenu (dynamic `ItemsSource=Rooms`
+ per-item routing via the `Host` pattern). Small quick-win that closes the ContextMenu
story and proves dynamic nested menus.

**Option 3 — Next Classroom Mode view** (~3–5 h)
Continue the UI port (e.g. a Teacher dialog or the main grid shell). **Needs the
Classroom Material theme ported first** (currently mapped ad-hoc to `Conf*`). Larger;
best after Option 1 lands the theme + icons.

**Option 4 (external track) — v1.2.1 Windows bulk-lock fix** (~2–3 h)
Customer priority (bulk lock 18/50 issue). **Separate from the macOS port** — shipped
Windows repo (`~/Dev/nty-classroom-macos/`), not this branch.

**Recommended sequence:** Option 2 (quick close-out) → Option 1 (icons + Classroom
theme, the real unblock) → Option 3. Option 4 is orthogonal, schedule by customer urgency.
