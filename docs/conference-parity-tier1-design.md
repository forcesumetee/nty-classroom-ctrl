# Conference Parity — Tier 1 Design (Phase 16-B)

**Status:** investigation (Phase 16-A). Step-by-step implementation plan
for Phase 16-B (Shared.Wpf extraction + UI bug fix + student gallery
unification).
**Parent:** [`conference-parity-architecture.md`](./conference-parity-architecture.md).
**Siblings:** [`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md),
[`conference-parity-tier3-design.md`](./conference-parity-tier3-design.md).
**Last updated:** 2026-05-29.

---

## Goal

After Phase 16-B:
- New WPF library `ClassroomCtrl.Shared.Wpf` houses the 4 Conference UI
  controls + 3 converters + 2 view-models.
- Teacher + Student.Agent both reference Shared.Wpf and reuse the same
  `ConferenceGalleryView` + `ConferenceTile` + `ConferenceToolbar` +
  `ConferenceSidebar`.
- Student-side `ConferenceGalleryWindow` hosts the real multi-tile
  gallery instead of the single-tile placeholder.
- The 15-C gallery rendering bug is fixed (teacher view shows the tiles
  correctly side-by-side).
- The student-side `Grid.Row` typo on the Leave bar is fixed.

Estimated effort: ~2-3 hr Claude Code time. Per-step commits.

## Pre-flight

- [ ] Phase 15-E `a6403c1` validated on 2-PC (or accepted as build-clean
      blocker for 16-A doc round).
- [ ] Working tree clean before step 1 (`.sln` edits otherwise conflict).
- [ ] Dev approves the Option A direction from 16-A architecture doc.

## Step 1 — Create `ClassroomCtrl.Shared.Wpf` project skeleton

**Files:** new `src/ClassroomCtrl.Shared.Wpf/ClassroomCtrl.Shared.Wpf.csproj`,
new directory tree.

```xml
<!-- src/ClassroomCtrl.Shared.Wpf/ClassroomCtrl.Shared.Wpf.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseWPF>true</UseWPF>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>ClassroomCtrl.Shared.Wpf</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClassroomCtrl.Shared\ClassroomCtrl.Shared.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
  </ItemGroup>
</Project>
```

Add to `ClassroomCtrl.sln` via `dotnet sln add`. Empty directories
created: `Conference/`, `ViewModels/`, `Converters/`.

**Commit:** `Phase 16-B step 1: ClassroomCtrl.Shared.Wpf project skeleton`

## Step 2 — Move converters

**Files moved:**

| From | To |
|---|---|
| `Teacher/Converters/BoolToVisibilityConverter.cs` | `Shared.Wpf/Converters/BoolToVisibilityConverter.cs` |
| `Teacher/Converters/NullToVisibilityConverter.cs` | `Shared.Wpf/Converters/NullToVisibilityConverter.cs` |
| `Teacher/Converters/ParticipantCountToColumnsConverter.cs` | `Shared.Wpf/Converters/ParticipantCountToColumnsConverter.cs` |

Namespace becomes `ClassroomCtrl.Shared.Wpf.Converters`. Teacher's other
XAML files that reference these converters need their
`xmlns:conv="clr-namespace:ClassroomCtrl.Teacher.Converters"` updated
**only where these 3 converters are used outside Conference views**.

Audit: grep `BoolToVisibilityConverter|NullToVisibilityConverter|ParticipantCountToColumnsConverter`
across `Teacher/**/*.xaml`. Plenty of MainWindow uses BoolToVisibility —
those files get a second `xmlns:convshared="clr-namespace:ClassroomCtrl.Shared.Wpf.Converters;assembly=ClassroomCtrl.Shared.Wpf"`
or we keep a thin re-export class in Teacher.Converters that derives
from the Shared.Wpf one. Recommend the **re-export shim** for
minimum-touch:

```csharp
// Teacher/Converters/BoolToVisibilityConverter.cs (after step 2 — shim)
namespace ClassroomCtrl.Teacher.Converters;
public class BoolToVisibilityConverter : ClassroomCtrl.Shared.Wpf.Converters.BoolToVisibilityConverter { }
```

This is a deviation from "no backwards-compat hacks" but the breadth of
XAML references (40+) makes it the lower-risk option. Document as a
**budget item** for cleanup in a future polish round.

**Commit:** `Phase 16-B step 2: move converters to Shared.Wpf + Teacher shims`

## Step 3 — Move view-models

**Files moved + split:**

`Teacher/ViewModels/ConferenceGalleryViewModel.cs` contains BOTH
`ConferenceTileViewModel` and `ConferenceGalleryViewModel`. Split + move:

| From | To |
|---|---|
| `ConferenceTileViewModel` class | `Shared.Wpf/ViewModels/ConferenceTileViewModel.cs` |
| `ConferenceGalleryViewModel` class | `Shared.Wpf/ViewModels/ConferenceGalleryViewModel.cs` |

Namespace: `ClassroomCtrl.Shared.Wpf.ViewModels`.

Teacher's `MainViewModel.cs` references:
- `ConferenceTileViewModel` — at [`MainViewModel.cs:130-131`](../src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs#L130-L131) (gallery rebuild),
  the `RecognizeHandCommand` declaration, [`OnHandRaiseReceived`](../src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs#L1797),
  reaction handlers, etc.

Add `using ClassroomCtrl.Shared.Wpf.ViewModels;` to MainViewModel.cs.
All type references continue to work.

**Commit:** `Phase 16-B step 3: move ConferenceTile + ConferenceGallery view-models to Shared.Wpf`

## Step 4 — Move Conference UI controls

**Files moved:**

| From | To |
|---|---|
| `Teacher/Views/Conference/ConferenceTile.xaml(+cs)` | `Shared.Wpf/Conference/ConferenceTile.xaml(+cs)` |
| `Teacher/Views/Conference/ConferenceGalleryView.xaml(+cs)` | `Shared.Wpf/Conference/ConferenceGalleryView.xaml(+cs)` |
| `Teacher/Views/Conference/ConferenceToolbar.xaml(+cs)` | `Shared.Wpf/Conference/ConferenceToolbar.xaml(+cs)` |
| `Teacher/Views/Conference/ConferenceSidebar.xaml(+cs)` | `Shared.Wpf/Conference/ConferenceSidebar.xaml(+cs)` |

**Per-file edits during move:**

1. `x:Class` attribute: change `ClassroomCtrl.Teacher.Views.Conference.X`
   to `ClassroomCtrl.Shared.Wpf.Conference.X`.
2. `xmlns:conv` namespace + assembly:
   `clr-namespace:ClassroomCtrl.Shared.Wpf.Converters;assembly=ClassroomCtrl.Shared.Wpf`
3. `xmlns:vm`: `clr-namespace:ClassroomCtrl.Shared.Wpf.ViewModels;assembly=ClassroomCtrl.Shared.Wpf`
4. `xmlns:local`: `clr-namespace:ClassroomCtrl.Shared.Wpf.Conference`

`ConferenceView.xaml` (Teacher-side outer shell) stays in Teacher —
it's the host. Update its `xmlns:local` to reference Shared.Wpf:

```xml
<!-- Teacher/Views/Conference/ConferenceView.xaml — after step 4 -->
<UserControl ...
  xmlns:confctrls="clr-namespace:ClassroomCtrl.Shared.Wpf.Conference;assembly=ClassroomCtrl.Shared.Wpf"
  ...>
  ...
  <confctrls:ConferenceGalleryView DataContext="{Binding ConferenceGallery}"/>
  <confctrls:ConferenceSidebar HorizontalAlignment="Right" Width="340" .../>
  <confctrls:ConferenceToolbar Grid.Row="1"/>
</UserControl>
```

**Commit:** `Phase 16-B step 4: move Conference UI controls to Shared.Wpf`

## Step 5 — Fix 15-C gallery UI bug

**File:** `Shared.Wpf/Conference/ConferenceGalleryView.xaml` + (likely)
`Shared.Wpf/ViewModels/ConferenceGalleryViewModel.cs`.

**Fix strategy:** drive `UniformGrid.Columns` from a computed VM property
rather than a binding-on-collection-Count inside ItemsPanelTemplate
(removes the H1 root-cause path entirely).

```csharp
// ConferenceGalleryViewModel.cs — additions

/// <summary>Phase 16-B step 5 — column count for the auto-layout
/// UniformGrid, computed from PageTiles.Count using the same
/// breakpoint table as the converter. Lives as a regular property
/// (not on Count) so bindings inside ItemsPanelTemplate resolve
/// reliably at realize-time AND re-evaluate on PageTiles changes.</summary>
public int ColumnsForPage
{
    get
    {
        int n = PageTiles.Count;
        if (n <= 1) return 1;
        if (n == 2) return 2;
        if (n <= 4) return 2;
        if (n <= 9) return 3;
        return 4;
    }
}

public void RebuildPageTiles()
{
    int start = (Math.Max(1, CurrentPage) - 1) * TilesPerPage;
    int end = Math.Min(Tiles.Count, start + TilesPerPage);
    PageTiles.Clear();
    for (int i = start; i < end; i++) PageTiles.Add(Tiles[i]);
    // Explicit notify so ColumnsForPage re-evaluates after each rebuild.
    OnPropertyChanged(nameof(ColumnsForPage));
}
```

```xml
<!-- ConferenceGalleryView.xaml -->
<ItemsControl.ItemsPanel>
    <ItemsPanelTemplate>
        <UniformGrid Columns="{Binding RelativeSource={RelativeSource AncestorType=ItemsControl},
                                       Path=DataContext.ColumnsForPage}"/>
    </ItemsPanelTemplate>
</ItemsControl.ItemsPanel>
```

`ParticipantCountToColumnsConverter` becomes unused after this change.
Leave it in Shared.Wpf for now (might be useful for other count-based
panels); deletion is a cleanup line-item.

**Verification:** add one-line Debug.WriteLine in the gallery view's
Loaded handler to log `ColumnsForPage` + realized panel type. Confirm
2-PC visual smoke shows side-by-side tiles for N=2.

**Commit:** `Phase 16-B step 5: fix gallery UniformGrid Columns binding (ColumnsForPage VM property)`

## Step 6 — Fix Student.Agent `ConferenceGalleryWindow` Leave bar Grid.Row typo

**File:** `Student.Agent/ConferenceGalleryWindow.xaml`.

At [`ConferenceGalleryWindow.xaml:81`](../src/ClassroomCtrl.Student.Agent/ConferenceGalleryWindow.xaml#L81): change
`<Border Grid.Row="1"` to `<Border Grid.Row="2"`. (RowDefinitions are
Auto / * / Auto — Leave bar belongs in Row 2.)

**Commit:** `Phase 16-B step 6: Student.Agent ConferenceGalleryWindow Leave bar Grid.Row typo`

## Step 7 — Student.Agent uses Shared.Wpf gallery

**Files:** `Student.Agent/ConferenceGalleryWindow.xaml(+cs)`,
`Student.Agent/ClassroomCtrl.Student.Agent.csproj`.

1. Add ProjectReference to Shared.Wpf:
   ```xml
   <ProjectReference Include="..\ClassroomCtrl.Shared.Wpf\ClassroomCtrl.Shared.Wpf.csproj" />
   ```
2. Build a thin `StudentConferenceShellViewModel` in
   `Student.Agent/ViewModels/StudentConferenceShellViewModel.cs` that
   wraps a `ConferenceGalleryViewModel` instance + exposes the shared
   commands (mic / cam / chat / reaction / hand) backed by IPC sends:
   ```csharp
   public partial class StudentConferenceShellViewModel : ObservableObject, IConferenceShellViewModel
   {
       public ConferenceGalleryViewModel ConferenceGallery { get; } = new();
       public IConferenceRole Role { get; } = new ParticipantRole();
       // ... shared commands routed via App.Ipc.SendAsync(Envelope...)
   }
   ```
3. Rewrite `ConferenceGalleryWindow.xaml` to host the Shared.Wpf
   `ConferenceGalleryView` + `ConferenceToolbar` + `ConferenceSidebar`
   slot pattern mirroring Teacher's `ConferenceView.xaml`. Remove the
   single-tile Image + the bottom Leave bar (toolbar handles Leave).
4. Code-behind: route inbound `CameraFrame` to the matching
   `ConferenceTileViewModel` in `Tiles` collection by EndpointId. Until
   16-C ships peer cam wire, only the teacher's EndpointId matches —
   single tile populates, rest stay placeholder. **This is the
   acceptable interim state**; 16-C lights up the rest.

**Commit:** `Phase 16-B step 7: Student.Agent hosts Shared.Wpf gallery + thin shell VM`

## Step 8 — Build + smoke test

- `dotnet build` all 3 projects (Teacher / Student.Agent /
  Student.Service) — 0 errors expected.
- Run T1-T16 wire-compat (no wire changes this round; should be no-op
  pass).
- Manual 2-PC smoke:
  1. Teacher starts Conference → gallery renders side-by-side tiles
     for self + force (UI bug FIXED).
  2. Student `ConferenceGalleryWindow` opens → shows gallery surface
     (multi-tile-capable) with teacher tile populated, rest placeholder
     (peer cam in 16-C).
  3. Teacher toolbar buttons work as before (mic / cam / chat / hand
     / more / end).
  4. Student-side toolbar shows but Conference admin buttons (End for
     All) NOT yet hidden (16-D ships role gating).
  5. No regression in Classroom mode (mode toggle reversible).

**Commit:** `Phase 16-B step 8: build + smoke acceptance` (or fold into
step 7 if no additional code changes needed).

## Acceptance checklist (8 items, 2-PC testable)

| # | Check | How to verify |
|---|---|---|
| 1 | `dotnet sln list` shows `ClassroomCtrl.Shared.Wpf` | shell |
| 2 | Teacher + Student.Agent both build with Shared.Wpf reference | `dotnet build` |
| 3 | Teacher Conference gallery shows N=2 tiles side-by-side | visual |
| 4 | Teacher gallery N=4 → 2×2 grid | visual |
| 5 | Student window now shows the real gallery surface | visual |
| 6 | Student-side Leave bar no longer overlays the tile area | visual |
| 7 | All existing Conference features still work (mic/cam/share/chat/hand/react) | manual click-through |
| 8 | Classroom mode unaffected — mode toggle round-trips | visual |

## Files touched (estimated)

**NEW (in Shared.Wpf):**
- `Shared.Wpf/ClassroomCtrl.Shared.Wpf.csproj`
- `Shared.Wpf/Conference/ConferenceTile.xaml(+cs)`
- `Shared.Wpf/Conference/ConferenceGalleryView.xaml(+cs)`
- `Shared.Wpf/Conference/ConferenceToolbar.xaml(+cs)`
- `Shared.Wpf/Conference/ConferenceSidebar.xaml(+cs)`
- `Shared.Wpf/ViewModels/ConferenceTileViewModel.cs`
- `Shared.Wpf/ViewModels/ConferenceGalleryViewModel.cs`
- `Shared.Wpf/ViewModels/IConferenceShellViewModel.cs`
- `Shared.Wpf/ViewModels/IConferenceRole.cs` (+ Host / Participant)
- `Shared.Wpf/Converters/{BoolToVisibility, NullToVisibility, ParticipantCountToColumns}Converter.cs`
- `Student.Agent/ViewModels/StudentConferenceShellViewModel.cs`

**MODIFIED:**
- `ClassroomCtrl.sln`
- `Teacher/ClassroomCtrl.Teacher.csproj` (ProjectReference + remove old files)
- `Student.Agent/ClassroomCtrl.Student.Agent.csproj` (ProjectReference)
- `Teacher/Views/Conference/ConferenceView.xaml` (xmlns + element refs)
- `Teacher/ViewModels/MainViewModel.cs` (using directives for Shared.Wpf VMs)
- `Teacher/Converters/{BoolToVisibility, NullToVisibility, ParticipantCountToColumns}Converter.cs` (re-export shims)
- `Student.Agent/ConferenceGalleryWindow.xaml(+cs)` (rewrite to host shared gallery)
- `Student.Agent/MainWindow.xaml.cs` (CameraFrame dispatch routes by EndpointId)

**DELETED (after move):**
- `Teacher/Views/Conference/{ConferenceTile, ConferenceGalleryView, ConferenceToolbar, ConferenceSidebar}.xaml(+cs)` (moved)

## Open questions for 16-B implementer

1. **Re-export shims for converters** — accepted? Or do we rename the
   Teacher refs in one sweep?
2. **`ConferenceView.xaml` (Teacher outer shell) — stays in Teacher?**
   Or moves to Shared.Wpf with a parameterized DataContext? Recommended:
   stays in Teacher; Student has its own outer (`ConferenceGalleryWindow`).
3. **`IConferenceShellViewModel` — where does it live exactly?** 16-A
   says Shared.Wpf/ViewModels. Confirmed unless deps disagree.

## After 16-B

Conference Mode visually parity-ready. Peer cam still missing — student
gallery has the tile slot for "force" student but no frames yet.
Proceeds to **16-C peer cam routing** ([`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md)) to light up
the empty slots.
