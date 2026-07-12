# Phase 25.1 Findings — ConferenceToolbar Port + Avalonia Animations

**Goal:** port the Conference bottom toolbar to Avalonia, introduce the Animations
subsystem, and fulfil the Phase 24.3 deferred reaction float-up (`TODO(Phase 25 or
later)`). **Result:** ✅ all done — toolbar renders with active states, reaction
Flyout wired, and the reaction float animation runs (captured mid-float over a tile
in `docs/phase-25.1-bottombar.png`).

**Sub-phases (per-commit):** 25.1-A layout + active states · 25.1-B reaction Flyout ·
25.1-C float animation (closes the 24.3 TODO) · 25.1-D integrated bottom-bar demo +
docs.

## What ported as-is / mechanically
- The whole toolbar is a `UserControl` binding to its host DataContext — **no
  `ConferenceToolbarViewModel` exists** in the shipped code. Modelled the exact
  binding contract in a sandbox `ConferenceToolbarDemoViewModel` (CommunityToolkit
  `[ObservableProperty]`/`[RelayCommand]`, unchanged idioms).
- Both button `ControlTheme`s from Phase 25.0-B (`ConfToolbarButtonStyle`,
  `ConfToolbarEndButtonStyle`) applied via `Theme=` — worked directly.
- `ToolTip="..."` → `ToolTip.Tip="..."` (mechanical).

## What required real translation (new patterns → cheat sheet §10, §11)
1. **Per-button `Style BasedOn` + `DataTrigger`s → conditional classes.** Each
   button's active/pending color became `.active`/`.pending` classes overriding the
   ControlTheme via its TemplateBindings. The Share button's **5 enum-value
   DataTriggers** collapsed to two VM-computed bools (`ShareIsActive`/`ShareIsPending`).
2. **WPF `Popup` → Avalonia `Button.Flyout`** — light-dismiss + placement built in,
   no code-behind `IsOpen` toggle. Reactions bind `Command`+`CommandParameter`
   directly instead of the shipped `Click`+`Tag`+reflection.
3. **WPF `Storyboard` → Avalonia `Animation`** (`KeyFrame`/`Cue`/`Setter`/`RunAsync`).

## What broke unexpectedly (the debug cost, all in 25.1-C/D)
The reaction float animation took **three iterations** — captured as cheat-sheet §10
gotchas so no one repeats them:
1. `rise.RunAsync(translateTransform)` → **`InvalidCastException`**: `RunAsync`
   targets a `Visual`, not a bare `Animatable`/transform.
2. Rewrote to animate `RenderTransform` via `TransformOperations` → **"No animator
   registered for RenderTransform"**: the `TransformOperationsAnimator` is `internal`
   and only auto-registers from the XAML path; `Animation.Animators` isn't public, so
   you cannot add it from code.
3. **Fix:** animate **`Margin.Top`** (`ThicknessAnimator` is registered by default) —
   a centered glyph with top margin `+20 → -80` rises cleanly, no transforms. Opacity
   keyframes (0→1@15%→1@70%→0) run in parallel on the same control.

Takeaway: **code-created animations are limited to properties with a default-
registered animator; `RenderTransform`/`translateY` keyframes are XAML-only** unless
you go through the (internal) transform animator.

## Justified simplifications (documented, Windows-only bits dropped)
- **PNG reaction assets** (`pack://` GDI+ images) → color emoji (Apple Color Emoji).
  The PNGs were a Win11-Thai-locale font-shaping workaround, irrelevant on macOS.
- **`LogEmojiDiagnostic()`** (GlyphTypeface probe + TEMP log) — dropped; Windows-only.
- **Reflection command resolution** → direct `Command` binding in the sandbox.
- **Flyout dismiss/placement not interactively verified** (renders in a popup layer
  the headless harness can't capture) — confirm by running the app.

## Verification
- `dotnet build` 0 errors after each sub-phase.
- **End-to-end proof:** the capture drives the *full* chain — toolbar
  `SendReactionCommand.Execute("👍")` → `ReactionPicked` event → tile
  `CurrentReactionEmoji` → `PropertyChanged` → `StartReactionFloat` → the 👍 caught
  mid-rise over the tile (opacity full, lifted above center). Toolbar states also
  correct (mic green, speaking ring on the neighbour tile, End red).

## Effort vs estimate
Estimated 2.5–3 h; actual work landed in range, with the animation gotchas
consuming the bulk of 25.1-C (as anticipated — "new subsystem"). The toolbar layout
+ states + Flyout were fast thanks to the ControlThemes and cheat sheet from 25.0.

## Notes for next ports
- The **animation gotchas generalize**: any code-driven motion should use
  `Margin`/`Canvas.Top`/`double`/`Color` (registered animators) or be authored in
  XAML. Batch remaining Storyboards (e.g. `NotificationOverlay`'s 11) with this rule.
- `MenuFlyout`/`ContextFlyout` is the path for `StudentCard`'s big `ContextMenu`;
  its `PlacementTarget.Tag` command routing needs redesign (cheat sheet §11).
- Consider promoting the headless-Skia capture harness (scratchpad `CaptureTile`,
  now with tab-select + reaction-trigger + animation-clock stepping) into `tools/`.
