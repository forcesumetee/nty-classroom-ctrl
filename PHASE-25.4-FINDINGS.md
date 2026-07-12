# Phase 25.4 Findings — StudentCard ContextMenu Port

**Goal:** port `StudentCard` (Teacher/Controls) + its ContextMenu — the pattern
Phase 24.3 flagged as the hardest — and **empirically answer** how ContextMenu
command routing works in Avalonia. **Result:** ✅ done. Card + full ContextMenu
render on macOS; routing solved and verified (`docs/phase-25.4-studentcard-menu.png`).

**Sub-phases (per-commit):** 25.4-A card layout/badges/selection · 25.4-B ContextMenu
structure · 25.4-C routing verification (the crux) · 25.4-D scenario + docs.

## The 4 questions you asked — answered empirically

### 1. Does `$parent[…].((vm:HostVm)DataContext).Command` work from a ContextMenu item?
**NO.** Headless route-test: 15 items realized, but every `Command` bound via
`$parent[ItemsControl].((vm:StudentGridDemoViewModel)DataContext).X` resolved to
**null**. `$parent` ancestor-walk **stops at the popup root** — a ContextMenu is a
separate visual tree, so it can't reach the main window's VM that way.

**Working fix:** the ContextMenu **inherits the target control's DataContext (the
item)**, so route via a **`Host` back-reference on the item**: `{Binding Host.X}`,
`CommandParameter={Binding}`. Verified — flat item `Lock` → `LastAction="Lock →
Somchai"` (correct param), nested `Group A` → `"Assign → Group A → Somchai"`.
WPF `PlacementTarget.Tag.XCommand` → Avalonia `Host.XCommand`. (Documented §13.)

### 2. Does cascading submenu behavior match WPF's cascade?
**Yes, functionally + visually.** A nested `<MenuItem>` renders the cascade **chevron
▸**, hover-opens, and its command resolves + fires (verified). No `ItemContainerStyle`
needed for static items. (Only the *dynamic* `ItemsSource=Rooms` submenu was deferred
per scope — a separate exercise, not a cascade-behavior problem.)

### 3. How does macOS render the menu icons?
**N/A for this menu — it has no icons** (text `Header`s only; no Segoe MDL2 anywhere
in StudentCard). The emoji that DO exist are in the *card body* (👑 🎤 👁 ✓ + badge
text) and render fine via **Apple Color Emoji**. No icon-system work done or needed
here; a proper icon/font strategy remains a separate Phase 25.5+ concern (as directed).

### 4. Is the right-click gesture equivalent?
**Equivalent and simpler.** Avalonia opens a `ContextMenu`/`ContextFlyout` on
right-click **automatically** — no code-behind. WPF needed the `Card_RightClick`
handler to stash the Window DataContext onto `Border.Tag`; that hack is **gone**.

## What ported as-is / mechanically
- ContextMenu structure 1:1: `MenuItem Header/Command/CommandParameter`, `Separator`,
  nested `MenuItem`. 17 items + 6 separators + 1 static nested submenu.
- Card body: badge stack, REC/👁 buttons, selection ring (base style + `.selected`).
- `HexToBrushConverter` ported (Avalonia `IValueConverter`, same shape); dynamic badge
  colors work. WPF `*Visibility` members → `bool` + `IsVisible`.

## Adaptations (documented)
- **Theme:** Classroom Material keys (`Surface.Card`, `Border.Default`, `Text.*`) →
  `Conf*` keys (the Classroom theme isn't ported). A future Classroom-theme port would
  swap these back.
- **Commands inert** (stamp `LastAction`) — routing *resolution + firing* is the point.
- **Deferred:** dynamic `Assign-to-Room` submenu (`ItemsSource=Rooms` +
  `ItemContainerStyle`), real command execution, thumbnail image, keyboard shortcuts.

## Bonus finding
**Avalonia `ContextMenu` popups DO render into headless `CaptureRenderedFrame`** (open
via `contextMenu.Open(target)` first) — unlike a `Button.Flyout` (25.1), which didn't.
So menu-open baselines are capturable headlessly. Added an `OpenFirstContextMenu`
helper + a `studentcard` scenario that captures the menu open.

## Tooling
- HeadlessCapture: new **`studentcard`** scenario (tab 5, menu open). Registry now:
  `theme`, `tile`, `bottombar`, `sidebar`, `studentcard`.
- Route-test harness (scratchpad) drives `contextMenu.Open` + invokes items to assert
  routing — reusable pattern for future menu verification.

## Cheat sheet growth
- **§13** ⚠️ "$parent does NOT cross a popup/ContextMenu boundary" + the `Host` fix.
- **§11** ContextMenu specifics (right-click auto, 1:1 structure, cascade ▸, popups
  render headlessly).
Now **14 sections** (content deepened; no new numbered section this phase).

## Verification
- `dotnet build` 0 errors each sub-phase; solution builds clean; T1–T26 still pass.
- Routing proven via headless route-test (Command non-null + LastAction fires, incl.
  submenu). Menu-open baseline: `docs/phase-25.4-studentcard-menu.png`.
- Shipped repo untouched.

## Effort vs estimate
Estimated 3–4 h; landed in range. The routing question (25.4-C) was the real work —
proving `$parent` fails in the popup, then landing the `Host` pattern — exactly the
"hardest flagged pattern" the phase set out to crack. The card body + menu structure
were mechanical thanks to prior patterns.

---

## Addendum — Phase 25.4-E: Dynamic `ItemsSource` submenu

Completed the deferred dynamic "Assign to room" submenu.

**Special question answered — does `MenuItem.ItemsSource` work like WPF? Mostly, but
the container-customization API differs.** WPF uses **`ItemContainerStyle`** (a
`Style`); Avalonia uses **`ItemContainerTheme`** (a `ControlTheme`, with `x:DataType`
for compiled bindings). Same Setters (`Header`/`Command`/`CommandParameter`). Applies
to any items host (MenuItem/ListBox/ItemsControl/TreeView). → new §11 entry.

**Verified (headless route-test):** `ItemsSource={Binding Host.Rooms}` → `ItemCount=3`,
3 room `MenuItem`s realized, `Header='Group A'`, `Command` non-null → invoke →
`LastAction="Assign → Group A"`. Empty-state: `IsEnabled={Binding Host.HasRooms}`;
`Rooms.Clear()` → parent disables (seen greyed out). Routing via `RoomDemo.Host`
(§13 pattern on the room item) — no `$parent` needed.

**BONUS finding — `$parent` DOES traverse the menu's own hierarchy.** A generated
submenu item reached its parent `MenuItem`'s DataContext (the student) via
`$parent[MenuItem].((vm:StudentCardDemoViewModel)DataContext).DisplayName` → resolved
**"Somchai"** (non-null). This **refines the 25.4-C rule**: `$parent` walks the
popup/menu's *internal* ancestor chain (submenu item → parent menu item = OK), but
**cannot escape the popup outward** to the host window's tree (that hop returns null →
use `Host`). Documented in §13.

**Headless-capture refinement:** the **top-level ContextMenu popup renders** into
`CaptureRenderedFrame`, but a **nested submenu popup does NOT rasterize** (parent
highlights ▸, child items absent). Verify dynamic submenu contents functionally.

**Cheat sheet:** §11 (`ItemContainerTheme` vs `ItemContainerStyle` + headless nested-
popup note) and §13 ($parent menu-hierarchy nuance).

## Next
- **Phase 25.5 candidate:** an **icon/font system** (the deferred concern) — Segoe
  MDL2 / Material glyphs used across Teacher views; decide on a cross-platform icon
  font or vector set before porting icon-heavy views.
- Or: dynamic `ItemsSource` submenu (finish the ContextMenu story), or the next
  Classroom-mode view (which will need the Classroom theme ported).
