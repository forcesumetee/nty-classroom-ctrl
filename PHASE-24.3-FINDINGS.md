# Phase 24.3 — First View Port Findings (ConferenceTile)

**Goal:** port ONE WPF view (`ConferenceTile`) to Avalonia AXAML on macOS, learn
the syntax gap empirically, produce a runnable Mac window, and use the real data
to re-estimate the full-port timeline.

**Result:** ✅ Ported view builds and renders on macOS (Apple Silicon). All state
paths verified in one screenshot — `docs/phase-24.3-conferencetile.png`:
HOST badge, "(You)" self-suffix, mic-live green dot, hand-raise badge, yellow
active-speaker ring, purple pinned ring, live-video frame path, reaction glyph.

**Files produced** (`src/ClassroomCtrl.Avalonia.Sandbox/`):
`ViewModels/ConferenceTileViewModel.cs`, `Views/ConferenceTile.axaml(.cs)`,
`Themes/ConferenceDarkTheme.axaml`, `ViewModels/MainWindowViewModel.cs`,
`MainWindow.axaml(.cs)`, `App.axaml`.

---

## 1. What ported as-is (zero changes)

- **The entire ViewModel logic.** `CommunityToolkit.Mvvm` (`ObservableObject`,
  `[ObservableProperty]` source generators, `partial void OnXChanged`, computed
  `Initial`) works **identically** under Avalonia. The generators run the same.
- **`{StaticResource}` lookups** — same markup extension, same semantics.
- **`{Binding Prop}`** for simple property bindings — same syntax.
- **`SolidColorBrush` + `Opacity`** in the resource dictionary — 1:1.
- **Layout primitives:** `Grid`, `StackPanel`, `Border`, `Ellipse`, `Image`,
  `TextBlock`, `WrapPanel`, alignment/margin/padding attributes — all identical.
- **`CornerRadius` / `Thickness` / `FontSize` resource values** — same element+
  content declaration syntax.

## 2. What changed but was purely mechanical (find/replace)

| WPF | Avalonia | Notes |
|---|---|---|
| `xmlns="…/winfx/2006/xaml/presentation"` | `xmlns="https://github.com/avaloniaui"` | root namespace |
| `.xaml` | `.axaml` | file extension convention |
| `<sys:Double>` (mscorlib clr-namespace) | `<x:Double>` | drops the `sys` xmlns |
| `Visibility="{Binding Bool, Converter=BoolToVisibility}"` | `IsVisible="{Binding Bool}"` | **converter deleted** — Avalonia uses a `bool IsVisible` |
| `System.Windows.Media.Imaging.BitmapSource` | `Avalonia.Media.Imaging.Bitmap` | the ONE VM type change |
| emoji `FontFamily="Segoe UI Emoji"` | (removed) | macOS resolves color emoji via Apple Color Emoji |

## 3. What required real translation (concepts that don't map 1:1)

1. **`Style.Triggers` / `DataTrigger` → conditional style classes.** This is the
   single biggest conceptual gap. Avalonia has **no** DataTriggers. The idiom is
   `Classes.foo="{Binding Bar}"` on the element + CSS-like selectors
   (`Selector="Border#OuterBorder.foo"`) carrying the setters. Ported all four
   tile triggers (live-bg, speaking, pinned) + the two icon triggers this way.
2. **`Visibility` (3-state enum) → `IsVisible` (bool).** Most converters simply
   vanish. For null checks, Avalonia ships built-ins:
   `ObjectConverters.IsNotNull` / `IsNull` (replaced `NullToVisibility` and its
   `Inverted` parameter) and `StringConverters.IsNotNullOrEmpty`.
3. **`DependencyProperty` → `StyledProperty`.** `AvaloniaProperty.Register<TOwner,T>()`
   + `GetValue`/`SetValue` wrapper. Analogous shape, different API; no
   `PropertyMetadata` needed for a simple default.
4. **Compiled bindings (`x:DataType`).** Not in WPF. Adopted the Avalonia
   best-practice `x:DataType` on the view + `DataTemplate` — turns binding typos
   into **compile errors**, which is a net win but requires element-name bindings
   (`{Binding #Root.SelfSuffix}`) for control-owned (non-VM) properties.
5. **Resource dictionary merge:** WPF `MergedDictionaries` with `Source="…"` →
   Avalonia `<ResourceInclude Source="avares://Assembly/Path.axaml"/>` (the
   `avares://` asset URI scheme).

## 4. What broke unexpectedly (cost debug time)

- **Local property values silently beat style setters** — cost the one real
  debug cycle. I first set `Background`/`BorderBrush`/`BorderThickness` as inline
  attributes on `OuterBorder`; the speaking/pinned **rings didn't render** because
  a locally-set value outranks a style setter (**identical to WPF's precedence
  rule** — the shipped `StudentCard.xaml` even documents it). Fix: move the base
  values into a base `Style Selector="Border#OuterBorder"` so the conditional-class
  styles can override them. **Lesson: any property a class/trigger toggles must
  have its base value in a style, never inline.**
- **Screenshotting a GUI app from a headless shell fails** (`screencapture:
  could not create image from display` — no screen-recording permission). Solved
  with Avalonia **headless Skia** off-screen rendering
  (`.UseSkia().UseHeadless(UseHeadlessDrawing=false)` + `CaptureRenderedFrame()`).
  One-time infra; reusable for future visual regression checks.
- Everything else compiled first try — including `<Run>` inline bindings, the
  merged dictionary, and `x:Double`/`CornerRadius`/`Thickness` resources.

## 5. Effort projection for the full port (grounded in real data)

**This view:** ~1.5–2 h of actual work (including the one ring bug + one-time
headless-capture harness). Discounting one-time infra, a *comparable* view is
**~1–1.5 h** now that the patterns are known.

**View inventory** (AXAML-portable, excluding App.xaml): **50 view files** —
Shared.Wpf 6, Teacher 28, Student.Agent 16 — 15 → 1251 lines each. Categorizing
by the constructs found here:

| Tier | Definition | Count (approx) | Per-view | Subtotal |
|---|---|---|---|---|
| **Simple** | banners/dialogs, static + few bindings (≤80 lines) | ~20 | 0.5–1 h | ~15 h |
| **Medium** | triggers + converters + resources, like ConferenceTile (80–300 lines) | ~22 | 1.5–3 h | ~50 h |
| **Complex** | ControlTemplates, ContextMenus, big composite windows (300+ lines): `MainWindow` (1251), `ConferenceSidebar` (558), `StudentCard` (ContextMenu), `ConferenceToolbar`, `GroupManagerView`, quiz windows | ~8 | 4–10 h | ~50 h |

- **View XAML total: ≈ 115 hours (~3 focused weeks).**
- **Plus non-view work** (from Phase 24.1 discovery): port `Networking`
  (registry→plist), `Licensing` (WMI/DPAPI→macOS), and the ~30–40% Windows-API
  rewrites — capture (ScreenCaptureKit), codec (VideoToolbox), audio
  (AVFoundation), policy enforcement/lock, tray, global hooks. This dwarfs the
  XAML and is the real critical path.

**Timeline read:** the **XAML port is NOT the bottleneck** — it's a bounded
~3-week slog with the patterns now known. The theoretical **6–8 month** estimate
is driven almost entirely by the platform-API rewrites, not the UI. This port
gives confidence the UI layer is *low-risk, high-throughput*; recommend the
timeline model weight risk onto the capture/codec/enforcement subsystems, not the
views. Nothing here suggests the 6–8 month range is wrong — but its *composition*
should shift toward native-API work.

## 6. Recommendations for the Phase 25 primer

1. **Build a "WPF→Avalonia cheat-sheet" first** (½ day) from §2–§3 so subsequent
   views are mechanical: trigger→classes, `Visibility`→`IsVisible`, converter
   deletions, DP→StyledProperty, `avares://` includes.
2. **Port the shared resource dictionaries early and in full.** `ConferenceDarkTheme`
   (36 keys) and the Teacher theme (`Surface.*`, `Text.*`, `Border.*` DynamicResource
   tokens) are dependencies of nearly every view. Do them once, up front.
3. **Codify the "base-values-in-a-style" rule** in the cheat-sheet and in review —
   it's the one non-obvious trap and it's invisible until you look at the render.
4. **Adopt `x:DataType` compiled bindings from day one** — the compile-time
   binding errors will catch the bulk of porting mistakes for free.
5. **Keep the headless-Skia capture harness** as a committed tool for
   screenshot/visual-regression of each ported view (no display permission needed).
6. **Sequence views simple→medium→complex**, and tackle the platform-API
   subsystems (capture/codec/enforcement) as their own phases — they, not the
   XAML, set the schedule.
7. **Defer animations as a batch.** Several views use Storyboards
   (`NotificationOverlay` has 11). Learn Avalonia's animation subsystem once, then
   sweep them together rather than per-view. (See the `TODO(Phase 25 or later)`
   marker in `ConferenceTile.axaml.cs`.)
