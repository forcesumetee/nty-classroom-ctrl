# HeadlessCapture

Reusable **off-screen Avalonia screenshot** tool (headless + Skia). Renders a view
or window to a PNG with **no display and no screen-recording permission** — works in
a plain shell, CI, or over SSH. Promoted from the Phase 24.3–25.1 scratchpad harness.

Used for visual verification / baselines of ported views (Phase 24.3 tile, 25.0-B
theme, 25.1 bottom bar…).

## Modes

### 1. Scenario registry (primary, reusable)
Named, self-documenting captures defined in [`Scenarios.cs`](Scenarios.cs). Each
records its description, originating phase, and the committed baseline it maps to —
so browsing that file shows the history + rationale of every screenshot.

```bash
# list all scenarios (name, phase, size, description, baseline)
dotnet run --project tools/HeadlessCapture -- --list

# capture one
dotnet run --project tools/HeadlessCapture -- --scenario theme --output docs/phase-25.0-B-theme.png
```

Current scenarios: `theme` (25.0-B), `tile` (24.3), `bottombar` (25.1-D).

### 2. Generic reflection (ad-hoc)
Screenshot any `Control`/`Window` (and optional parameterless VM as `DataContext`)
by fully-qualified type name — no scenario needed:

```bash
dotnet run --project tools/HeadlessCapture -- \
  --view-type ClassroomCtrl.Avalonia.Sandbox.Views.ThemeShowcaseView \
  --output /tmp/showcase.png --width 940 --height 1000
```
The type must live in a **referenced** assembly (the Sandbox is referenced) and have
a **parameterless constructor**. Views needing constructor args or complex VM state →
add a scenario instead.

## CLI reference
| Flag | Meaning | Default |
|---|---|---|
| `--list` | Enumerate scenarios and exit | — |
| `--scenario <name>` | Capture a registered scenario | — |
| `--view-type <FQN>` | Ad-hoc: build + capture a Control by type name | — |
| `--vm-type <FQN>` | (with `--view-type`) parameterless VM as DataContext | — |
| `--output <path>` | PNG output path | `capture.png` |
| `--width N` / `--height N` | Window size | scenario's, or 800×600 |
| `--theme light\|dark` | Theme variant | App default (Dark) |

## How to add a scenario for a future phase
In [`Scenarios.cs`](Scenarios.cs), add an entry (short, memorable name):

```csharp
["sidebar"] = new()
{
    Description = "ConferenceSidebar participants+chat tabs — Sandbox tab 3.",
    OriginalPhase = "25.x",
    OriginalScreenshot = "docs/phase-25.x-sidebar.png",
    Width = 1000, Height = 900,
    BuildWindow = () => new MainWindow(),
    AfterShow = w =>
    {
        CaptureRunner.SelectTab(w, 3);
        // ...inject VM state / step animations via CaptureRunner helpers...
    },
};
```
Helpers available in [`CaptureRunner.cs`](CaptureRunner.cs): `SelectTab`,
`StepAnimation`, `AsWindow`, `SetTheme`.

## Determinism & baselines (important)
- **Static scenarios (`theme`, `tile`) are deterministic in content** — reliable for
  visual review. Note: PNG encoding / anti-aliasing can cause **small byte-size
  differences** run-to-run even when the image is visually identical, so **compare
  visually, not by byte-equality / hash**.
- **Animated captures (`bottombar`) are timing-variant.** Avalonia's headless clock
  advances on **real elapsed wall-time**, so the reaction glyph's exact position
  depends on scheduling. Good for illustration; **not** a pixel-diff regression gate.
- **Baseline provenance:**
  - `theme` → reproduces `docs/phase-25.0-B-theme.png` (content-identical).
  - `bottombar` → reproduces `docs/phase-25.1-bottombar.png` (structure identical;
    float position varies).
  - `tile` → the committed `docs/phase-24.3-conferencetile.png` is **historical**:
    it was captured *before* the Sandbox gained its `TabControl` (760×520, no tab
    strip). The `tile` scenario captures the **current** tabbed app (940×1000), so
    it legitimately differs from that historical image. The historical file is kept
    as an audit-trail artifact and intentionally **not** overwritten.

## Common gotchas

### Gotcha A — `--no-build` shows a stale UI after a Sandbox change
- **Symptom:** you edit a Sandbox view/VM, re-capture, and the screenshot still
  shows the OLD state.
- **Root cause:** `dotnet run --no-build` skips the build, so the tool runs against
  the **cached copy** of `ClassroomCtrl.Avalonia.Sandbox.dll` in its own output
  folder. Rebuilding the *Sandbox* project alone does **not** refresh that copy —
  only building the *tool* re-copies the dependency DLL.
- **Fix:** after any Sandbox change, run **without** `--no-build`
  (`dotnet run --project tools/HeadlessCapture -- --scenario …`), or explicitly
  `dotnet build tools/HeadlessCapture` first.
- **Detection tip:** if a capture "doesn't reflect my change," suspect this first.

### Gotcha B — Debug vs Release path mismatch
- **Symptom:** `--no-build` errors with *"No such file or directory"* / can't find
  the binary; or a capture reflects a build from the other configuration.
- **Root cause:** Debug and Release write to **different** output paths
  (`bin/Debug/net10.0` vs `bin/Release/net10.0`). Building one config and running the
  other with `--no-build` uses a stale/absent binary.
- **Fix:** keep the config consistent — build and run with the **same** `-c`
  (`dotnet run -c Release …`), and avoid mixing a bare `dotnet build` (Debug) with a
  `-c Release --no-build` run.

## Requirements
`net10.0`, `Avalonia.Headless` + `Avalonia.Skia` (12.1.0), a project reference to the
assembly holding the views (currently `ClassroomCtrl.Avalonia.Sandbox`).
