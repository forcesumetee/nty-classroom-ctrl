# TT-2 LIVE Confirmation — a real Windows Student appears in the Mac Teacher window

**Date:** 2026-07-14 · **Milestone:** TT-2 (windowed Teacher — live student grid) · **Gate:** *a real
student appears as a tile in a real Mac Teacher window, and the grid updates on join/leave.*

## Result: PASS — via the harder network-cut path
The **windowed `Avalonia.Teacher` app** launched on the Mac, a **real Windows Student** connected over
the LAN and **appeared as a tile** in the grid, and **cutting the PC's network** made the tile
**disappear** — the stale-sweep path, not a clean socket close.

- ✅ Mac Teacher app launches; the header shows the listening address (`<LAN-IP>:7777`).
- ✅ Real Windows Student (PC) connects → a student tile appears in the grid (green presence dot + name/machine); count → "1 student connected"; empty state hidden.
- ✅ **Network cut** on the PC → after the 15 s stale-sweep, the tile disappears; count → "No students connected"; the "Waiting for students…" empty state returns.

## Why the network-cut path is the strong proof
A clean disconnect (window-close on the student) closes the socket immediately. **Cutting the network
leaves a half-open socket** — the realistic flaky-classroom-wifi failure. It exercises, end to end:
- **the 15 s stale-sweep** firing through the transport → `ControlServer` → `StudentRoster` → the UI (not a socket-close event);
- **`StudentRemoved` marshaled to the UI thread** (TT-2-C) — an un-marshaled build would have raced the render loop *right here*, off the UI thread, mid-draw;
- **the namespace-gap fix** removing the **right** tile (by EndpointId, not the transport peerId — the TT-1-D correctness work);
- **the empty state** returning.

So TT-1's roster-correctness work is now **visibly proven in a window**, via the failure mode that
actually happens in classrooms.

## Environment
- **Teacher:** MacBook Air (Apple Silicon), `Avalonia.Teacher` via `dotnet run --project
  src/ClassroomCtrl.Avalonia.Teacher` → `TeacherSession` hosting the real `Teacher.Core` on `0.0.0.0:7777`.
- **Student:** a real Windows PC running the shipped v1.2 product, unmodified, pointed at the Mac's IP.
- **LNP:** passed — `dotnet run` inherited Terminal's local-network grant (no prompt).

## Headless companions (committed gates)
- `MockStudent --teacherselftest` — 20/20 (real-client interop + roster fix + ownership guard + stale-sweep + teardown).
- TT-2-B launch smoke 5/5 · TT-2-C **the marshal proof** 14/14 (background raise + UI-thread mutation) · TT-2-D grid render + empty state + socket teardown 10/10.

## Pattern consistency
Every milestone M15–M23 and TT-1 had a LIVE gate that caught what headless tests didn't. TT-2 holds it:
the headless suites prove the wiring + the marshal; **this LIVE run proves the grid reflects a real
shipped client, through the stale-sweep failure path.** The Mac Teacher now has a real window.

## Known gaps (not blockers for TT-2)
- No per-student screen view yet (TT-3) and no thumbnails (TT-4 decode).
- Context menu / bulk actions / live badges deferred (see `TT-2-FINDINGS.md`).
- Bundled Teacher (TT-13) will still need `NSLocalNetworkUsageDescription`.
