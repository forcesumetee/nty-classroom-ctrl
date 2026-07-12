# Phase 25.5 Findings — Icon Strategy + Classroom Material Theme

**Goal:** design a cross-platform icon strategy (Segoe MDL2 → alternative) and port the
Classroom Material theme. **Result:** the icon question **dissolved on inspection**
(no icon system needed), and the Classroom **color tokens** are ported + verified
(`docs/phase-25.5-classroom-theme.png`).

**Sub-phases:** 25.5-A research · 25.5-B `Colors.Light` port · 25.5-C showcase + icons
+ scenario · 25.5-D docs.

## Icon strategy: NONE NEEDED (the headline finding — diagnose-before-build win)
Repo research, not assumption:
- **Segoe MDL2 / Segoe Fluent: 0 usages.**
- **`PackIcon` / Material *icons*: 0 usages.** `MaterialDesignThemes` is referenced for
  the **theme** (`BundledTheme` + `MaterialDesign3.Defaults`), **not** icons.
- **Actual icons = emoji + Unicode symbols** (🎙 🎬 📋 📚 🔔 ⚙ 🔒 🛡 📤 🎥 👑 🎤 👁 ✋ +
  ✓ ✕ ⋮ ➤ ⚙). These render **natively on macOS** (Apple Color Emoji + system fonts).

**Verified visually** (25.5-C showcase + every capture since 24.3): color emoji render
in color; Unicode symbols render and accept a `Foreground` tint (✓ green, ✕ red, ➤
blue). **Recommendation adopted: Option E — no icon font/library.** Saved the 2–4 h
icon-system build + a dependency. Documented in cheat sheet §14 (incl. the "PNG
reaction fallback was a Windows-only bug — do NOT port" note).

## Classroom Material theme: it's a SET of dictionaries, not one file
The shipped theme = MaterialDesign base (`BundledTheme` + `MaterialDesign3.Defaults`)
**+** custom dicts in `Shared/Themes/`:

| Dict | Keys | This phase |
|---|---|---|
| **Colors.Light.xaml** | **36** | ✅ **Ported** → `ClassroomLightTheme.axaml` |
| Colors.Dark.xaml | 36 | Deferred (runtime swap) |
| Spacing / Typography / Effects | 12 / 8 / 2 | Deferred (value tokens; port when a view binds them) |
| Buttons / Inputs / DataDisplay | 9 / 4 / 10 | Deferred — **WPF Styles over MaterialDesign controls; don't port 1:1** (Avalonia re-themes controls via ControlThemes/FluentTheme) |

**Ported (25.5-B):** the 36 semantic color tokens — Surface / Border / Accent /
Semantic / Text / Status — names + hex verbatim. `<Color>` + `<SolidColorBrush
Color="{StaticResource X.Color}">` port ~1:1. **No key collision** with
ConferenceDarkTheme (`Conf*` vs `Surface.*`/`Text.*`/…); both merged in App.axaml and
coexist (cheat sheet §15).

## Verification (25.5-C)
`ClassroomShowcaseView` (light surface) renders all 6 tiers + a sample card built from
the tokens (Surface.Card + Border.Default + Text.* + Accent.Primary + 🔒/💬 buttons) +
the icon showcase. Both themes visibly coexist (dark Conf tabs vs light Classroom tab).
`classroom` HeadlessCapture scenario added. Registry now: `theme`, `tile`, `bottombar`,
`sidebar`, `studentcard`, `classroom` (6).

## Cheat sheet growth
14 → **16 sections**: §14 Icon strategy (none needed) + §15 Multiple theme dictionaries
coexisting.

## Scope adherence
- Icon system: none built (Option E, per approval).
- Theme: Colors.Light core (36) only; Dark/value-token/control-style dicts deferred
  and documented.
- Sandbox only; Wire referenced; shipped repo untouched; per-sub-phase commits.

## Effort vs estimate
Estimated ~2 h (down from 2–4 h once the icon system proved unnecessary); landed there.
The research (25.5-A) delivered the biggest value by removing scope.

## Next
- **Classroom control styles** (Buttons/Inputs/DataDisplay → Avalonia ControlThemes) —
  the larger remaining theme effort, needed for pixel-faithful Teacher dialogs.
- Or the next **Classroom-mode view** (now that Colors.Light tokens exist to bind).
- Or **runtime light/dark theme swap** (port Colors.Dark + a theme manager).
- External: v1.2.1 Windows bulk-lock fix.
