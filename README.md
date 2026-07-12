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
dotnet build ClassroomCtrl.Avalonia.sln
dotnet run --project tools/EnvelopeWireCompatTest -c Release   # expects all T1–T26 PASS
```
