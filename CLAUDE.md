# NTY ClassroomCtrl — macOS Port (Phase 24+)

## Project context

macOS port of NTY ClassroomCtrl — classroom management software for Thai 
schools. Original Windows version (v1.2) is shipped and in customer use.

**Original Windows product:**
- Tech: .NET 10, WPF, net10.0-windows
- Status: v1.2 shipped, customer using
- Wire protocol: T1-T26 stable
- Features: Classroom Mode, Conference Mode, Multi-select, Bulk Actions

**macOS port goal:**
- Support both Teacher and Student on macOS (Apple Silicon primary)
- Same wire protocol → cross-platform interop with Windows
- Use Avalonia UI 12.1 (WPF-like, cross-platform .NET)
- Reuse business logic + wire library, port UI (XAML → AXAML)

## Approach: Avalonia cross-platform

**Reusable from Windows codebase (~60-70%):**
- Wire protocol library (envelopes, MessagePack): 100% reusable
- MVVM ViewModels: mostly reusable
- Business logic: mostly reusable

**Needs rewrite (~30-40%):**
- Views (XAML → AXAML, ~90% syntax same)
- Windows-specific APIs → macOS equivalents:
  - DXGI capture → ScreenCaptureKit
  - MediaFoundation → AVFoundation
  - WTSDisconnectSession → NSWorkspace/overlay
  - Registry → NSUserDefaults / plist

## Dev environment

- MacBook Air M2 (Apple Silicon, macOS Sequoia 15)
- .NET 10.0.301
- Avalonia Templates 12.1.0
- VS Code 1.128.0
- Git 2.39.5
- Homebrew 4.5.8

## Current branch

- Working branch: v1.2-multiselect
- HEAD: 81fbf09 Phase 23 step 7 (EN+TH localization)
- Latest shipped: fix-issues @ fb8a2e1 (Phase 22.5-D)

## Phase 24 roadmap

**Phase 24.1: Foundation (~1-2 hours)**
- Create separate Avalonia solution folder
- Port Shared.Wire library (should be pure C#, minimal changes)
- Change TargetFramework: net10.0-windows → net10.0
- Verify: dotnet build succeeds on macOS
- Run T1-T26 wire compat tests on macOS

**Phase 24.2: First view port (~2-3 hours)**
- Port ONE simple view: StudentTile or LoginView
- Understand XAML → AXAML syntax differences empirically

**Phase 24.3: Connection prototype (~2-3 hours)**
- macOS app connects to Windows Teacher (v1.2)
- Verify cross-platform wire interop working

**Phase 25+: Full port (TBD after Phase 24)**

## Critical constraints

- DO NOT modify shipped Windows codebase in this repo
- Create NEW folder for Avalonia work (e.g. ../nty-classroom-avalonia/)
- Wire protocol T1-T26 unchanged
- Preserve MessagePack envelope structure exactly (for interop)

## Coding conventions

- .NET 10 target
- Nullable enabled
- File-scoped namespaces
- Async/await for I/O
- CommunityToolkit.Mvvm for ObservableProperty + RelayCommand

## AI workflow

- Diagnose before fix (evidence-based)
- Per-step commits (reversible)
- Build verify after each change
- Ask before major changes / new files
- Use dotnet CLI, not IDE-specific commands (works cross-platform)
