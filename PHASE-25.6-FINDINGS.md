# Phase 25.6 Findings — Classroom Design System + ApplyPolicyDialog (execution log)

**Goal:** apply the established foundation to port a real Classroom-mode view. **Result:**
✅ the flagship `ApplyPolicyDialog` renders on macOS using `ClassroomLightTheme` + a
newly-ported design system (`docs/phase-25.6-applypolicy.png`). This was **execution,
not discovery** — a largely mechanical port.

**Sub-phases:** 25.6-B design system · 25.6-C ApplyPolicyDialog · 25.6-D docs.

## Key upfront finding (reshaped the plan)
Every real Teacher dialog depends on a **shared design system** — `Button.Primary/
Secondary/Danger/Success/Ghost/Icon` + `Text.H1/H2/H3` (from the deferred Buttons/
Typography dicts) + a header/body/footer shape. So 25.6-B ported that system first
(unblocks all future dialogs), then 25.6-C ported a dialog on top.

## 25.6-B — design system (`ClassroomControls.axaml`)
Pure application of documented patterns:
- WPF keyed button `Style`s → Avalonia **`ControlTheme`s** on a shared `ClassroomButtonBase`
  (rounded template + `:pointerover`/`:pressed`/`:disabled`), applied via
  `Theme="{StaticResource Button.X}"` (§3e — same as `ConfToolbarButtonStyle`).
- WPF keyed `TextBlock` `Style`s (`Text.H*`) → **style classes** `Classes="h1"` (§3).
- Bundled as a `<Styles>` file (ControlThemes in `Styles.Resources` + the text-class
  `Style`s); included via `StyleInclude`. Verified: all 7 variants + H1/H2/H3.

## 25.6-C — ApplyPolicyDialog (the mechanical port)
- Header/body/footer, `ClassroomLightTheme` tokens, design-system buttons + `h2` title.
- **Standard Avalonia CheckBox/ComboBox/TextBox** applied directly (MaterialDesign
  `*.Default` styles deferred) — bound to a demo VM (`ApplyPolicyDemoViewModel`);
  Apply/Revert/Cancel stamp `LastAction`. Loc `DynamicResource` → literals.
- **The one new nugget: `ThemeVariantScope RequestedThemeVariant="Light"`.** The app root
  is Dark (for Conference tabs); wrapping the light dialog in a light scope makes Fluent's
  variant-aware controls (CheckBox/ComboBox/TextBox) render legibly (dark text on light).
  Cleaner than the "leave incongruent" fallback. Documented in cheat sheet §16.

## Success criteria (all met)
- ✅ Structurally correct (header/body/footer).
- ✅ Buttons use the new ControlThemes; text uses the new classes.
- ✅ Usable + congruent (checkboxes/textboxes legible via ThemeVariantScope).
- ➖ Full accent-exact control theming = deferred (the larger "Classroom control styles" effort).

## Execution-thesis verdict
**Confirmed.** With 16 cheat-sheet sections + both themes + the design system, porting a
real dialog was mostly mechanical translation (theme tokens, `Theme=`/`Classes=`, layout,
standard controls, Loc→literals). The only judgment call — light controls in a dark app —
had a clean standard answer (`ThemeVariantScope`), applied + documented without a stall.

## Cheat sheet growth
16 → **17 sections**: §16 ThemeVariantScope (light views in a dark-default app) + the
design-system reuse note.

## Tooling
HeadlessCapture registry now **7 scenarios**: theme, tile, bottombar, sidebar,
studentcard, classroom, **applypolicy**.

## Next
- **Classroom control styles** (CheckBox/ComboBox/TextBox/etc. → Avalonia ControlThemes
  matching the Classroom accent) — the remaining theme-polish effort; now scoped by this port.
- More Teacher dialogs (roster, branding, quiz…) — now unblocked by the design system;
  ClassRoster additionally needs a `ListView`/`GridView` → `ListBox`+Grid or DataGrid decision.
- Runtime light/dark theme swap · external v1.2.1 Windows fix.
