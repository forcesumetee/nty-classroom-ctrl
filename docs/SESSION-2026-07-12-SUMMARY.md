# Session Summary — 2026-07-12

macOS/Avalonia port of NTY ClassroomCtrl. This session built the **foundation and
first UI**: extracted the wire protocol, **proved cross-platform interop on real
Windows hardware (byte-perfect)**, ported the ConferenceTile + ConferenceToolbar
(the Conference bottom bar), introduced the Avalonia Animations subsystem, and
produced reusable porting tooling (cheat sheet + full theme).

**Repo:** `~/Dev/nty-classroom-avalonia/` (separate from the shipped Windows repo
`~/Dev/nty-classroom-macos/`, which was **never modified** — verified clean after
every phase). Branch: `avalonia-experiment` (remote `origin`:
github.com/forcesumetee/nty-classroom-ctrl).

---

## Phases completed (24.1 → 25.1)

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

## Commit list (this session)

```
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
(plus the Phase 25.1 closeout commit)

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

### Cheat sheet growth (living doc)
Grew to **12 numbered sections** with real, tested examples: added §3e
(keyed-Style→ControlTheme, 25.0-B), **§10 Animations** and **§11 Popups/Flyouts**
(25.1); references renumbered to §12. Unverified rows still explicitly tagged.

### Tools built (reusable)
- **T1–T26 wire compat harness** (`tools/EnvelopeWireCompatTest`) — runs on macOS.
- **Cross-platform interop client** (`tools/CrossPlatformInteropTest`).
- **`tools/HeadlessCapture`** (Phase 25.2) — reusable off-screen screenshot tool, no
  display / screen-recording permission. Scenario registry (`theme`/`tile`/`bottombar`,
  self-documenting) + generic `--view-type` reflection + `--list`. Add a scenario per
  future port. (Promoted from the scratchpad `CaptureTile` harness.)
- **WPF→Avalonia cheat sheet** — living document (12 sections).
- **Full ConferenceDarkTheme** + theme showcase baseline.
- **ConferenceTile + ConferenceToolbar** ported (Conference bottom bar) with a
  working reaction float animation.

### Constraints (verified honored)
- MessagePack pinned exactly `2.5.187` for byte-compat (NU1902/NU1903 advisories
  are expected and must NOT be "fixed" by upgrading — see memory note).
- Shipped Windows repo untouched throughout.

## Status of prior pending items

- [x] **Real Windows-Teacher interop verification** — ✅ **DONE today**, byte-perfect
  on real hardware (see Phase 24.2 row). Cross-platform product line VALIDATED
  end-to-end.
- [x] **Phase 25.1 (ConferenceToolbar)** — ✅ done (25.1-A…D).
- [ ] **v1.2.1 bulk lock fix** — Windows product, **separate track**, not part of the
  macOS port. Tracked here only so it isn't forgotten.

## Next-session preparation notes

- Open the cheat sheet (`docs/WPF-TO-AVALONIA-CHEATSHEET.md`) first — it's the
  porting playbook.
- Sandbox app: `dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox`
  (MainWindow is a TabControl: theme tokens + tile).
- Build the whole solution: `dotnet build ClassroomCtrl.Avalonia.slnx`.
- Promote the headless-Skia capture harness from scratchpad into `tools/` so view
  screenshots are reproducible in-repo.
- The full `ConferenceDarkTheme` (incl. both toolbar button `ControlTheme`s) is
  already available app-wide via `App.axaml` — ConferenceToolbar can consume it
  directly.

---

## Next session (Phase 25.3) — pick ONE (not started)

_(The former Option 1 — promote the capture harness — was done as Phase 25.2.)_

**Option A — `ConferenceSidebar`** (~2–3 h)
Continue the Conference arc (tile → theme → toolbar → **sidebar**). Introduces
`TabControl` (participants/chat) + chat-list virtualization. 558 lines (Complex
tier). Value: completes the Conference surface (bottom + side). Add a `sidebar`
scenario to `tools/HeadlessCapture/Scenarios.cs` for its baseline.

**Option B — `StudentCard` ContextMenu** (~3–4 h)
Tackle the large `ContextMenu` → `MenuFlyout`/`ContextFlyout` while cheat-sheet §11
is fresh; redesign the WPF `PlacementTarget.Tag` command routing. 242 lines but the
hardest pattern flagged in the 24.3 findings. Value: unlocks Classroom Mode UI.

**Pre-read for either:** the cheat sheet (`docs/WPF-TO-AVALONIA-CHEATSHEET.md`) —
§10/§11 are the newest and most relevant; use `tools/HeadlessCapture` for the baseline.

**Also queued (separate track):** v1.2.1 Windows bulk-lock fix; customer conversation
on the macOS timeline (use the §"Timeline data" numbers).
