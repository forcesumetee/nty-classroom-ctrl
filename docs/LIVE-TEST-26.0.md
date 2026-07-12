# Live Test — Phase 26.0 (Mac Sandbox → Windows Teacher)

The Mac-side work is complete and **self-verified locally** (`MockTeacher --selftest`
PASS). This is the one-shot procedure to confirm real cross-platform interop against the
**shipped Windows Teacher v1.2**. Expected time at the Windows PC: minutes.

## Prerequisites
- Both machines on the **same LAN** (the Phase 24.2 setup: iPhone hotspot subnet).
- Windows PC running **Teacher v1.2**.

## Procedure
1. **Windows:** launch Teacher v1.2. Note its **IPv4** (`ipconfig` → the hotspot adapter;
   was `172.20.10.7`) and confirm the channel (default `1234`) + TCP port `7777`.
2. **Mac:** run the Sandbox —
   `dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox` — open the **Connection** tab.
3. Enter the Teacher **IP** (and port 7777 / display name as desired) → click **Connect**.
4. **Verify:**
   - [ ] Sandbox status pill turns **green "Connected"**; traffic log shows
         `System → Connecting/TCP connected`, `TX ▲ Hello`, then `TX ▲ Ping` every 5 s.
   - [ ] **Windows Teacher shows the Mac as a student tile** (display name = what you typed).
   - [ ] From Teacher, **Lock** the Mac → Sandbox self-tile shows the red
         "🔒 Screen locked by teacher" banner + an `RX ▼ LockScreen` log row.
   - [ ] From Teacher, **Apply Policy** → Sandbox self-tile shows the policy **chips** +
         an `RX ▼ PolicyApply` row with the decoded summary.
   - [ ] (Optional) Teacher **chat broadcast** → `RX ▼ ChatBroadcast` + chat line on the tile.
   - [ ] Click **Raise hand** on the Mac → the Teacher tile lights the hand indicator (S→T).
5. **Screenshot the moment** — the Mac tile inside Windows Teacher + the Sandbox reflecting
   a Lock/Policy. This is the Phase 26.0 milestone asset (save as
   `docs/phase-26.0-live-windows.png`).

## If the IP changed
Just enter the new IPv4 in step 3 — nothing else changes. (Discovery-by-beacon is not
required; direct-IP connect is the primary path.)

## Troubleshooting
- **No "Connected":** Windows Firewall may block inbound TCP 7777 → allow it for Teacher,
  or confirm both machines are on the same subnet (`ping` the Teacher IP from the Mac).
- **Connected but no tile:** confirm the Teacher is v1.2 and `ProtocolVersion` = 1 (it is).
- **Reflects nothing:** check the traffic log — if `RX` rows appear, dispatch works; if the
  Teacher never sends, re-issue the Lock/Policy from its UI.

## What this proves
Byte-compatible, bidirectional wire interop between a **macOS/Avalonia** student and the
**shipped Windows/WPF** Teacher — the full-stack goal of the port. Capture (screen/cam/audio)
and enforcement remain Phase 27+.
