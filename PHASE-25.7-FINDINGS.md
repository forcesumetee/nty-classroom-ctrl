# Phase 25.7 Findings — More Classroom Dialogs (execution log)

**Goal:** prove the design system scales to *multiple* dialogs without stalling — and
resolve the flagged `ListView`/`GridView` decision. **Result:** ✅ three more dialogs
ported (one list, one form, one input-with-validation), all mechanical.

**Sub-phases:** 25.7-A ClassRosterManager · 25.7-B CameraSelectorDialog ·
25.7-C TeacherIPDialog · 25.7-D docs.

## The one real decision — `ListView`/`GridView` → **templated `ListBox`** (§17)
Avalonia has no `ListView`/`GridView`/`GridViewColumn`. Roster is **display-only** (4 read
columns, no sort/edit) → **`ListBox` + `Grid` `ItemTemplate`** with a header row whose
`ColumnDefinitions` **match** the item Grid. **No new dependency.** The only easy-to-miss
step: rows need `HorizontalContentAlignment=Stretch` (on `ListBoxItem`) or the `*` column
won't align with the header. Selection highlight is free from the Fluent `ListBoxItem`
theme. **DataGrid deferred** to when a list needs built-in sort/resize/inline-edit (new
NuGet + theme include) — rule: *display-only → ListBox; interactive grid → DataGrid.*
This **sets the precedent for every future list view** (docs/…§17 is the reference).

## 25.7-A ClassRosterManager (list) — the only novelty
Header/body(list)/footer. Footer = 6 design-system buttons in a `DockPanel` (Danger +
I/O left, proceed right) — proves the multi-button footer idiom too. Thai class names
render natively (system font). Baseline `docs/phase-25.7-roster.png`.

## 25.7-B CameraSelectorDialog (form) — pure mechanical
Labelled device `ComboBox` (ItemsSource-bound) + a 2-col `Grid` of resolution/fps combos.
Zero new patterns. Baseline `docs/phase-25.7-camera.png`.

## 25.7-C TeacherIPDialog (input + validation) — first **Student-side** view
TextBox + hint + inline error `TextBlock` (`Accent.Danger`), shown only on failure via
`StringConverters.IsNotNullOrEmpty` → `IsVisible` — **the reference pattern for every
future input dialog.** Baseline captured mid-validation (`docs/phase-25.7-teacherip.png`).

**IP validation pattern (ported verbatim, per request):** **IPv4 dotted-quad `Regex`
(`RegexOptions.Compiled`) AND `System.Net.IPAddress.TryParse`** — belt-and-suspenders,
because `TryParse` *alone* permissively accepts `"10.0.0"` and octal `"0700.0.0.1"`; the
regex pins the canonical four-decimal-octet form. No third-party library. This is the
reference validation approach for future input dialogs (regex-shape-check + framework
parse, both required).

## Execution-thesis verdict (multi-dialog)
**Confirmed at scale.** Three dialogs, three interaction shapes (list / form / validated
input), one real decision (the list), zero stalls. The design system + `ClassroomLightTheme`
+ `ThemeVariantScope` carried all three; the only new institutional knowledge is §17.

## Tallies
- Cheat sheet: 17 → **18 sections** (§17 list-rendering; Reference links → §18).
- HeadlessCapture registry: 7 → **10 scenarios** (`roster`, `camera`, `teacherip`).
- MainWindow tabs: 8 → **11** (tabs 8/9/10). First Student-side view landed.

## Next (unchanged options, now better-scoped)
- **Classroom control theming** (CheckBox/ComboBox/TextBox/ListBoxItem → accent-exact
  Avalonia ControlThemes) — the remaining polish; now exercised by 4 dialogs' worth of controls.
- More dialogs (Branding/GroupManager are heavier, ~200+ lines) · editable Attendance grid
  (the empirical `DataGrid` trigger) · runtime light/dark swap · external v1.2.1 Windows fix.
