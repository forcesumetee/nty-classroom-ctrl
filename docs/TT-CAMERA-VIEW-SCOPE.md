# Camera View (teacher watches a student's camera) — READY-TO-BUILD SCOPE
**Status: NOT built (2026-07-14). Deliberately abandoned after recon** — there is no clean 1:1 path
without touching the frozen `Shared.Wire` or the conference relay (which is TT-11). This doc is the
full recon so the next session (with a Mac) doesn't re-investigate. **The teacher currently CANNOT
open a student's camera** — record that gap; it is NOT done.

## Why it wasn't buildable in a 60-min box (the blocker)
The student→teacher camera path is **`ConferenceCameraFrame` (0x068x)**, and the ControlServer's
inbound handler **AUTO-RELAYS every frame to all peers** — `ControlServer.cs:1692`:
```csharp
case MessageType.ConferenceCameraFrame:
    ConferenceCameraFrameReceived?.Invoke(this, (env.SenderId, frameMsg));   // event (0 subscribers today)
    var frameRelay = Envelope.Create(MessageType.ConferenceCameraFrame, env.Payload, env.SenderId);
    _ = _tcp.BroadcastAsync(frameRelay, ...);                                 // ← STAR RELAY = TT-11
```
Same auto-relay for `ConferenceCameraStart` (:1676) and `ConferenceCameraStop` (:1702). So using this
path for a 1:1 "monitor one student" would **broadcast the monitored student's camera to the entire
class** — a privacy leak AND the relay routing that is explicitly out-of-scope (TT-11). The clean
alternatives both hit a hard constraint: a **new targeted camera-request wire type = ❌ Shared.Wire
change** (14-session freeze); **making the relay conditional = surgery on the conference core = TT-11**.

## The wire map (frozen Shared.Wire — do NOT change)
| Family | Tags | Direction | Notes |
|---|---|---|---|
| `CameraStart/Frame/Stop` | 0x0460/1/2 (Phase 9.5) | **teacher → students** | the teacher's OWN webcam to the class. `Messages.cs:424` "CameraStart (9.5) is teacher→student". **Wrong direction** for monitoring a student. Stop is **reliable** (port `ControlServer.cs:504`, shipped `:460`) ✅. |
| `ConferenceCameraStart/Frame/Stop` | 0x068x (Phase 16-C) | **participant → T → all peers (RELAYED)** | each participant emits its OWN cam; teacher relays to peers (star). `JpegData` payload (`ConferenceCameraFrameMessage`). This is TT-11 peer-cam. |
| `ConferenceStart/End` | 0x0670/1 | teacher → all | starts/ends Conference; the student's `CameraStreamer` streams ONLY while a Conference session is active (`ConnectionViewModel` gates on the session id). `BroadcastConferenceStartAsync` is reliable-broadcast (`ControlServer.cs:587`). |

## The TT-9 trap IS present here
`ControlServer.ConferenceCameraFrameReceived` (`:573`) and `ConferenceCameraStartReceived` (`:568`)
exist but have **ZERO teacher-side subscribers** (grep across `src/ClassroomCtrl.Avalonia.Teacher` =
none). So a student's conference-camera frames are **decoded and dropped today** — identical to the
TT-9 audio situation before TT-9-C. The receive+display half is genuinely net-new.

## Recommended path (needs a Mac + one wire/relay decision)
**Recommendation: fold camera into TT-11 (conference), not a standalone 1:1 monitor** — the whole
infra (relay, sessions, peer-cam) is conference-shaped, and the teacher gallery showing all cams is
the natural fit. Build a standalone "monitor one student's camera" ONLY if the customer specifically
requires it apart from conference — and that requires a wire/relay decision from the wire owner:
- **Trigger:** a targeted `ConferenceStart` (add a *targeted* overload to `ControlServer` — port-side,
  **no wire change**) makes ONE student stream. But the inbound relay (above) still fans its frames to
  all peers → you must EITHER make the relay conditional (only in a real multi-peer conference) OR add
  a non-relayed targeted camera-request type (**Shared.Wire change — needs sign-off**).
- **Receive + display (net-new, reusable, no wire):** subscribe `ConferenceCameraFrameReceived` →
  decode → show. **Reuse:** `ClassroomCtrl.Avalonia.Media` (frames are **JPEG** → `ScreenFrameDecoder.DecodeMjpeg`);
  mirror the `ScreenViewController` + `ScreenViewModel` + `ScreenViewWindow` pattern (TT-3/TT-4) and the
  per-tile action pattern (the TT-9 "Listen to mic" toggle is the exact sibling).
- **Net-new total:** teacher-side subscriber + a camera window + the trigger decision. **Effort M**,
  IF folded into TT-11; the standalone monitor is gated on the wire decision.

## 🔴 PRIVACY flags for whoever builds it (do not skip)
1. **A dropped camera STOP that leaves the student's CAPTURE running = a student filmed unaware** —
   categorically worse than #5's stuck viewer. **Verify the capture-stop trigger is RELIABLE:**
   `ConferenceEnd` (reliable-broadcast) → student `CameraStreamer.StopAsync`. The teacher's own
   `CameraStop` 0x0462 is reliable ✅. **BUT the `ConferenceCameraStop` RELAY is LOSSY**
   (`ControlServer.cs:1702` `BroadcastAsync`) — a dropped relay leaves a **stale peer tile** (cosmetic;
   the source capture already stopped) — still, **trace the source-side capture-stop and confirm it's
   reliable end-to-end. Potential Windows bug #8 — VERIFY, don't assume.**
2. **Student-visible "camera live" indicator: ALREADY EXISTS** — `SelfTile.IsCameraLive` renders a red
   badge in the Sandbox `Views/ConnectionView.axaml:199`. So "nobody filmed silently" is satisfied for
   the conference-cam path; **confirm it lights for whatever trigger the monitor uses.**

## Gate (when built)
`--teacherselftest`: teacher opens student A's camera → A streams, **B does NOT** (the distinguishing
negative, same shape as every per-student feature). Assert it.
