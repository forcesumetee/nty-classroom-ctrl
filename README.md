# NTY ClassroomCtrl — Avalonia / macOS port

Cross-platform port of NTY ClassroomCtrl (classroom management for Thai schools).
The shipped product is Windows/WPF (.NET 10); this solution rebuilds it on
Avalonia so Teacher and Student can also run on macOS (Apple Silicon primary)
while staying **wire-compatible** with the Windows v1.2 clients.

> The shipped Windows codebase lives in a separate repo (`nty-classroom-macos`)
> and is **never modified** by this port. Source files are *copied* here.

## Phase 24.1 — Wire protocol extraction (this commit)

`ClassroomCtrl.Shared.Wire` is the pure C# wire-protocol library carved out of
the Windows-locked `ClassroomCtrl.Shared` assembly. It targets plain `net10.0`
(no `-windows`, no WPF, no DirectX) so it builds on macOS.

**Byte-compatibility is the contract.** MessagePack is pinned to `2.5.187` and
the envelope key layout is preserved exactly, so macOS clients produce
byte-identical envelopes to the shipped Windows clients.

### Layout
```
src/ClassroomCtrl.Shared.Wire/
  Protocol/      Envelope, MessageType, Messages, Constants
  Models/        Pure C# domain models (no Windows deps) + Roster/
  Localization/  EN/TH text resources + Loc accessor
  Discovery/     BeaconPayload (UDP discovery wire type)
  Telemetry/     TelemetryPayload
  Attachments/   AttachmentManager (chat file attachments, cross-platform IO)
tools/EnvelopeWireCompatTest/   T1–T26 wire round-trip / cross-version tests
```

### Build & verify
```bash
dotnet build ClassroomCtrl.Avalonia.slnx
dotnet run --project tools/EnvelopeWireCompatTest -c Release   # expects all T1–T26 PASS
```

## Phase 24.2 — Cross-platform interop proof

`tools/CrossPlatformInteropTest` is a macOS console app that talks to the
**shipped Windows Teacher (v1.2)** over the real wire protocol, using only
`ClassroomCtrl.Shared.Wire`. It reproduces the shipped Student's transport
behavior exactly (verified against `ClassroomWorker` + `TcpControlServer`):

- **Scenario A — Discovery:** binds UDP 7778 and decodes the Teacher's
  fire-and-forget `BeaconPayload` broadcast (map-mode MessagePack). Proves a Mac
  decodes Windows-produced bytes, and auto-discovers `TeacherIp:TcpPort`.
- **Scenario B — TCP handshake:** connects to Teacher:7777, framing is
  `[4-byte big-endian Int32 length] + MessagePack(Envelope)`. Sends `Hello`
  (as the shipped Student does) then `Ping`; the Teacher's transport auto-replies
  `Pong` at the connection level regardless of app state, so a returned `Pong`
  is a **deterministic** proof of round-trip interop.

### Test procedure (against a real Windows Teacher)
1. On the Windows PC: launch Teacher v1.2 (note its ChannelId, default `1234`).
2. On the Mac (same LAN):
   ```bash
   # Auto-discover the Teacher via its beacon, then handshake:
   dotnet run --project tools/CrossPlatformInteropTest -c Release
   # Or target it directly, skipping discovery:
   dotnet run --project tools/CrossPlatformInteropTest -c Release -- --teacher-ip <IP>
   ```
3. Expect `=== RESULT: interop PROVEN ✅ ===` (exit 0). The Teacher log should
   show `Student joined: MacInteropTest (...)`.

Options: `--teacher-ip <IP>`, `--port <n>` (7777), `--channel <id>` (1234),
`--discover-timeout <ms>`, `--listen-only`, `--skip-discovery`.

> Verified on macOS against a loopback mock that independently implements the
> same beacon + auto-Pong behavior. The real Windows-Teacher run is the final
> confirmation and requires the Windows box on the same LAN.
