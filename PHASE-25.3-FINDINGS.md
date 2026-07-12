# Phase 25.3 Findings — ConferenceSidebar Port + Full Conference Composition

**Goal:** port the ConferenceSidebar (Chat + Participants tabs), then compose all
three ported Conference components into one window. **Result:** ✅ done — the full
Conference Mode UI (tile grid + toolbar + sidebar) renders on macOS
(`docs/phase-25.3-conference-full.png`). **Milestone 9.**

**Sub-phases (per-commit):** 25.3-A shell + tabs + lists · 25.3-B input + raised-hands
+ role-gated Mute/Recognize · 25.3-C full composition + `sidebar` scenario · 25.3-D docs.

## What ported as-is / mechanically
- **Chat bubbles reuse the REAL ported `ChatMessage` model** (`Shared.Wire`) —
  `SenderName`/`MessageText`/`HasAttachment`/`AttachmentIcon`/`AttachmentSizeFormatted`
  (the last via the ported `AttachmentManager.FormatSize`). No demo chat model needed.
- Theme keys (`ConfSidebar*`, `ConfChat*`, etc.) already ported in 25.0-B — consumed directly.
- Thai text + emoji render correctly on macOS (`สวัสดีครับ 👋`).

## Two assumptions the source corrected (both simplified the work)
1. **Not a `TabControl`.** The shipped sidebar hand-rolls tabs (toggle buttons +
   `Visibility`-gated bodies) — so this was the known classes + `IsVisible` pattern
   plus one `Style`→`ControlTheme` (`SidebarTabPill`), not a new TabControl subsystem.
   Rationale + "when to hand-roll vs use TabControl" → cheat sheet **§12**.
2. **No virtualization.** Both lists are plain `ItemsControl` in a `ScrollViewer`
   (WPF `ItemsControl` doesn't virtualize; the `ScrollViewer` wrap disables it). So
   there was nothing to port — Avalonia `ItemsControl` matches (also non-virtualizing).
   **Deferred** as planned; at 1000+ messages swap to Avalonia `ListBox` (virtualizes
   by default) — a Phase 25.4+ option. No rabbit hole.

## Patterns discovered (the durable value) → cheat sheet §13
- **`$parent[UserControl]` = WPF `RelativeSource AncestorType=UserControl`** (FindAncestor).
- **Compiled-binding cast:** inside a `DataTemplate` (item `x:DataType`), reaching the
  *ancestor's* `DataContext` members requires an explicit cast, since it's statically
  `object`:
  ```
  $parent[UserControl].((vm:ConferenceSidebarDemoViewModel)DataContext).MuteParticipantCommand
  ```
  Verified: both `IsVisible` role-gating and `Command` bindings resolve this way.
  WPF didn't need the cast (late-bound). **This is the exact idiom `StudentCard`'s
  ContextMenu will need** — forward-referenced in §13.
- **SidebarTabPill** is the 3rd `ControlTheme`; organized in `ConferenceDarkTheme.axaml`
  under a `// Sidebar` section (toolbar ones under `// Toolbar`) — grep by section.
- **`Visibility`-typed model member** (`HandRaisedVisibility`) → `IsVisible` bool
  (`HandRaised`) — the WPF participant VM exposed `Visibility` directly.

## Manual-tabs decision (documented per request)
Faithful to the shipped sidebar, which hand-rolls tabs. Also the right call here: the
pill/underline chrome is trivial as style classes, whereas matching it inside a
`TabControl` would require a full `ControlTemplate` override for a 2-tab surface. Not
a blanket rule — see §12 for when a real `TabControl` is the better choice.

## Gotchas caught → tools/HeadlessCapture/README.md "Common gotchas"
- **A — `--no-build` stale DLL:** after a Sandbox change, `--no-build` runs against
  the cached `Sandbox.dll` in the tool's output; rebuilding the Sandbox alone doesn't
  refresh it. Fix: run without `--no-build` (or build the tool). Cost two confused
  captures this phase.
- **B — Debug/Release path mismatch:** building Debug then running
  `-c Release --no-build` uses a stale/absent binary. Fix: keep `-c` consistent.

## Scope adherence
- Attachment card **rendered** (icon/name/size) with **inert** Download/Open (picker
  deferred, as scoped). Attach 📎 in the input row is inert too.
- Deferred (unchanged): file-picker dialog, emoji picker, real network dispatch,
  virtualization.

## Cheat sheet growth
10 → **14 sections**: added §12 (manual tabs vs TabControl) and §13 (advanced binding
scopes); references renumbered to §14. Living-doc footer updated.

## Verification
- `dotnet build` 0 errors after each sub-phase; full solution builds clean.
- HeadlessCapture registry: `theme`, `tile`, `bottombar`, **`sidebar`** (new).
  Regression check: all prior scenarios still produce output.
- Screenshot: `docs/phase-25.3-conference-full.png` (1280×800) — the composed window.

## Effort vs estimate
Estimated ~2.5–3 h; actual landed in range. The `$parent` compiled-binding cast was
the only real head-scratcher (a few minutes to land the cast syntax); the rest reused
established patterns (classes/IsVisible, ControlTheme, ItemsControl templates).

## Next
- **Phase 25.4 candidate: `StudentCard` ContextMenu → MenuFlyout** — the hardest
  flagged pattern; §13 (`$parent` + cast) and §11 (flyouts) are the groundwork.
- Or continue: other Classroom-mode views; or wire real state to the Conference demo.
