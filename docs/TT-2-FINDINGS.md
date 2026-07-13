# TT-2 Findings — the windowed Teacher: a live student grid · COMPLETE (LIVE-confirmed)

**Goal:** the first windowed `Avalonia.Teacher` view — a student grid that shows real students as
tiles, live, driven by the TT-1 `StudentRoster`. **LIVE gate met: a real Windows Student appears as a
tile in a real Mac Teacher window, and disappears when its network is cut** (the stale-sweep path).
Constraints held: `src/ClassroomCtrl.Avalonia.Teacher` + copies only, **Sandbox untouched**, Shared.Wire
unchanged, **shipped Windows repo untouched**.

## The Session-1 shell payoff
The student-grid UI was **already ported** 12 sessions ago as inert Sandbox showcases (`StudentCard.axaml`
+ code-behind, `StudentGridDemoViewModel`, the 15-action ContextMenu, `HexToBrushConverter`, the theme
dictionaries). TT-2 was therefore **"wire the proven shell to the real roster,"** not "build a grid" —
a much smaller job. TT-2 copied the card + converter + theme into the Teacher app (Sandbox left intact),
made the tile VM observable, and fed it from `StudentRoster`.

## THE MARSHAL — the load-bearing item (record this pattern)
`StudentRoster` raises `StudentAdded` / `StudentRemoved` on **the transport's background read loops**
(accept loop / per-peer `PeerConnection.RunAsync`, both `Task.Run`). Avalonia's `ObservableCollection`
**must** be mutated on the **UI thread** — the render loop reads it concurrently.

**Exact location:** [`TeacherGridViewModel`](../src/ClassroomCtrl.Avalonia.Teacher/ViewModels/TeacherGridViewModel.cs)
— its two roster-event handlers each wrap the mutation in `Dispatcher.UIThread.Post`:
```csharp
private void OnStudentAdded(object? sender, StudentRoster.Entry e) =>
    Dispatcher.UIThread.Post(() => { … Students.Add(new StudentTileViewModel(…)); … });
private void OnStudentRemoved(object? sender, Guid endpointId) =>
    Dispatcher.UIThread.Post(() => { … Students.Remove(tile); … });
```

**Why a naive test misses it:** an un-marshaled mutation (`Students.Add` straight in the handler) **still
updates the collection** — a "count went 0→1" test passes. It just runs on the **wrong thread**, racing
the render loop → an intermittent crash in production, at 50 seats, exactly when the grid is being drawn.
This is the LIVE failure mode TT-2-E exercised (network-cut → stale-sweep fires `StudentRemoved` off-UI).

**The thread-assertion pattern (use for any future roster→UI binding):** capture the dispatcher context
at BOTH ends —
- at the **raise site** (a `StudentAdded`/`StudentRemoved` subscription): `Dispatcher.UIThread.CheckAccess() == false` (proves the event fires on a background thread);
- at the **`CollectionChanged` site**: `Dispatcher.UIThread.CheckAccess() == true` (proves the mutation lands on the UI thread).

Trigger the event by connecting a raw client + Hello **from a `Task.Run`**. An un-marshaled build goes
red on the second assertion. This is the same **§20 pattern** as the native callbacks (M16–M22) — the
**8th** time we've needed background→UI marshaling; the discipline is now proven across native P/Invoke
callbacks AND managed transport threads.

## Project structure
- `src/ClassroomCtrl.Avalonia.Teacher` is a **windowed** app (NOT `LSUIElement`, unlike the Student
  menubar Sandbox). References Teacher.Core.
- `StudentCard` + `HexToBrushConverter` + `ConferenceDarkTheme.axaml` were **copied** from the Sandbox,
  not shared — a shared-UI lib is premature at two consumers, and copying kept the Sandbox untouched
  (Student track can't regress). The Sandbox showcase and the Teacher's production card can now diverge.
- `TeacherSession` encapsulates server + roster + grid so the app wiring is unit-testable.

## Socket-teardown discipline
`TeacherSession.Dispose` (idempotent) releases `:7777` on **window-close / app-quit**, wired in
`App.axaml.cs`. This matters: during TT-1-F a leftover TeacherHost left `:7777` bound and the next run
hit "Address already in use." The teardown is asserted headlessly (started session → Dispose → re-bind
on the same port succeeds → `BoundPort` null) — the same assertion TT-1-B/E used.

## Presence affordance
Each tile shows a **green presence dot** (`StudentTileViewModel.PresenceColorHex` → `HexToBrush`). The
TT-1 roster is binary (present = in the collection), so a live tile is `Connected`; presence *changes*
are shown by tiles appearing/disappearing. The enum leaves room for `Reconnecting`/`Stale` if a future
phase surfaces a grace-period. This is what makes TT-1's correctness work (namespace-gap fix, ownership
guard, stale-sweep) **visible** in the LIVE test.

## Deferred (honest, with reasons)
- **ContextMenu wired to real actions** — "right-click-does-nothing is worse than no menu"; needs the
  `ControlServer` send-layer (15 actions). A later per-student-actions phase.
- **Screen thumbnails** — TT-3/TT-4 (needs H.264 **decode**).
- **Multi-select + bulk toolbar** — later (a re-port of Windows Phase 23).
- **Live badges** (mic / webcam / hand / room) — phase in once wired; the badge XAML is already in the
  Sandbox shell.

## LNP — carry to the bundle
The app binds `:7777` and accepts LAN connections → the Local Network Privacy gate. Via `dotnet run` it
inherits **Terminal's** grant (LIVE passed, no prompt). **The bundled Teacher (TT-13) will still need
`NSLocalNetworkUsageDescription`** — the dev path hides it; carry 32-F's guard into the first Teacher bundle.

## Verification (all green)
Headless: TT-2-B launch (5/5), TT-2-C tile VM + card render + **the marshal proof** (14/14), TT-2-D grid
render + empty state + teardown (10/10). Non-regression: full solution build 0 errors; **T1–T27 PASS ·
`--selftest` PASS · `--teacherselftest` 20/20**; Sandbox untouched. **LIVE:** real Windows Student → tile
in the Mac window; network-cut → stale-sweep → tile gone.

## What TT-2 unlocks · next
The Mac Teacher now has a **real window with a live student grid**. **TT-3** — the per-student screen
view UI (the tile's screen area + a full-screen student view), then **TT-4** — H.264 **decode**
(`VTDecompressionSession`, the one big new native piece) + the **scale gate** (30–40 tiles decoding).
The `--teacherselftest` gate + the `--classroom N` harness (TT-0) are ready for both.
