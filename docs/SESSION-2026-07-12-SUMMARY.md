# Session Summary — 2026-07-12

macOS/Avalonia port of NTY ClassroomCtrl. This session built the **foundation**:
extracted the wire protocol, proved cross-platform interop (client-side), ported
the first view, and produced reusable porting tooling (cheat sheet + full theme).

**Repo:** `~/Dev/nty-classroom-avalonia/` (separate from the shipped Windows repo
`~/Dev/nty-classroom-macos/`, which was **never modified** — verified clean after
every phase). Branch: `main`.

---

## Phases completed (24.1 → 25.0-B)

| Phase | Deliverable | Outcome |
|---|---|---|
| **24.1** | Extract wire protocol as `ClassroomCtrl.Shared.Wire` | Carved the pure protocol out of the Windows-locked `Shared` assembly. net10.0, MessagePack **2.5.187 pinned**. **T1–T26 all pass on macOS.** |
| **24.2** | Cross-platform interop client (`tools/CrossPlatformInteropTest`) | macOS console app: UDP beacon decode + TCP Hello/Ping→Pong handshake. **Proven end-to-end against a loopback mock** (exit 0). Real Windows-Teacher run pending (user hardware). |
| **24.3** | First view port — `ConferenceTile` (Avalonia Sandbox app) | Renders on macOS; all state paths verified. Found + codified the "local value beats style setter" trap. |
| **25.0-A** | `docs/WPF-TO-AVALONIA-CHEATSHEET.md` | Reusable porting reference grounded in the real port; unverified claims tagged. |
| **25.0-B** | Full `ConferenceDarkTheme` port (36 keys) + theme showcase | All 36 keys, names preserved verbatim; keyed `Style`→`ControlTheme` pattern learned + added to cheat sheet §3e. |

## Commit list (this session)

```
4d9c52b  Phase 25.0-B: Full ConferenceDarkTheme port (36 keys)
ba414c4  Phase 25.0-A: WPF->Avalonia cheat sheet
4c27199  Phase 24.3: First view port — ConferenceTile
5d79569  Phase 24.2: Cross-platform interop test (macOS -> Windows Teacher)
97e1051  Phase 24.1: Extract wire protocol as ClassroomCtrl.Shared.Wire
```
(plus this closeout commit)

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

### Tools built (reusable)
- **T1–T26 wire compat harness** (`tools/EnvelopeWireCompatTest`) — runs on macOS.
- **Cross-platform interop client** (`tools/CrossPlatformInteropTest`).
- **Headless-Skia screenshot recipe** — off-screen render to PNG, no display /
  screen-recording permission needed (used for all baselines). Lives in scratchpad
  as `CaptureTile`; worth promoting to a committed tool next session.
- **WPF→Avalonia cheat sheet** — living document.
- **Full ConferenceDarkTheme** + theme showcase baseline.

### Constraints (verified honored)
- MessagePack pinned exactly `2.5.187` for byte-compat (NU1902/NU1903 advisories
  are expected and must NOT be "fixed" by upgrading — see memory note).
- Shipped Windows repo untouched throughout.

## Pending items

1. **Real Windows-Teacher interop verification (Phase 24.2 closeout).** Run on the
   Windows PC: `dotnet run --project tools/CrossPlatformInteropTest` (auto-discover)
   or `-- --teacher-ip <IP>`. Expect `RESULT: interop PROVEN ✅`; Teacher log should
   show `Student joined: MacInteropTest (...)`. Loopback proof gives ~95% confidence.
2. **Phase 25.1 direction** — recommended: **ConferenceToolbar** (see planning note
   below).
3. **v1.2.1 bulk lock fix** — Windows product, **separate track**, not part of the
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

## Phase 25.1 planning note (NOT started — next session)

**Target: `ConferenceToolbar`** (Shared.Wpf/Conference/ConferenceToolbar.xaml, 345 lines).

**Why it's the right next step**
- Both button `ControlTheme`s (`ConfToolbarButtonStyle`, `ConfToolbarEndButtonStyle`)
  are already ported and proven — the toolbar is their primary consumer.
- Continues the Conference Mode learning arc (tile → theme → toolbar → sidebar).
- Introduces the **Avalonia Animations subsystem** (the toolbar/reaction popup +
  the deferred `ConferenceTile` reaction float — see `TODO(Phase 25 or later)` in
  `ConferenceTile.axaml.cs`). First real animation port; batch the learning.
- Medium tier (~1.5–3 h per the effort matrix), consistent with ConferenceTile.

**Pre-read before starting**
- `src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceToolbar.xaml` (+ `.xaml.cs`)
  in the shipped repo — expect: toolbar buttons, active-state toggles (mic/cam/
  share on), possibly a "more" popup/menu, and any Storyboard animations.
- Its ViewModel dependency footprint (likely `StudentConferenceShellViewModel` /
  toolbar commands) — assess portability as we did for ConferenceTile.

**Expected new patterns to capture in the cheat sheet**
- Avalonia **Animations / Transitions** (vs WPF Storyboards) — the big new subsystem.
- Possibly **Popup / Flyout** (if the toolbar has a "more" menu) — WPF Popup/
  ContextMenu → Avalonia `Flyout`/`MenuFlyout`.

**Alternatives considered (deferred):** simple views (TeacherIPDialog) — too trivial
to learn from now; Student.Agent shell — bigger, better after more view experience.
