# Teacher Track Roadmap — macOS ClassroomCtrl Teacher (Avalonia)

**Status:** TT-0…TT-5 COMPLETE + LIVE-confirmed (2026-07-14) — MockStudent harness · Teacher.Core
(transport + router + roster) · windowed Teacher (live student grid) · per-student **live screen view
(MJPEG + H.264 decode)**, proven against BOTH a shipped, unmodified Windows Student (OpenH264→VT
interop) and a Mac Student (our M18 VideoToolbox H.264 — customer B's path) · **core commands
(lock/unlock + power)** — a Mac Student is hard-locked (M21 kiosk), a Windows Student locks + logs off,
power platform-gated, every command on the reliable channel. **Both biggest risks are now retired:**
the SCALE gate (TT-3 — screens are on-demand, 1–4 concurrent, not a 40-tile wall, §7) and the BITSTREAM
(TT-4 — one uniform Annex-B Baseline shape from every student, §5/§6). What remains (TT-6…TT-13) is
**known work with no research risk**, except the TT-10 system-audio-loopback investigation (§6/§7).
**TT-6 next** (multi-select + bulk actions, v1.2). The Mac Teacher can now **see AND command** students.
Hand-off doc for a parallel shift.
**Author context:** drafted 2026-07-13 after Phase 31-B (Student track), grounded in a
structural map of the shipped Windows Teacher (`/Users/fewfee/Dev/nty-classroom-macos`,
v1.2.1, .NET 10 / WPF). **That shipped repo is READ-ONLY — copy from it, never modify it.**

---

## 1. Purpose & how to read this

The Student track (Milestones 17–22) is producing a **shippable macOS Student** that talks to
the shipped Windows Teacher. This roadmap is the mirror image: a **macOS Teacher** (Avalonia)
that the shipped **Windows Students** connect to — unlocking the cross-platform scenarios where
the teacher is on a Mac.

Each phase below is **independently LIVE-testable against real shipped Windows Students** and
ends in a per-phase commit, mirroring the Student-track rhythm (investigate → approve → build →
structural harness → LIVE → close-out). Phases are numbered **TT-0 … TT-13** ("Teacher Track")
to avoid collision with the **T1–T27** wire-compat cases (`tools/EnvelopeWireCompatTest`), which
are a different thing and stay unchanged.

**Golden rule (inherited):** the wire protocol T1–T27 and the MessagePack `Envelope` are frozen.
The macOS Teacher must be byte-compatible with shipped Windows Students, exactly as the macOS
Student is byte-compatible with the shipped Windows Teacher.

---

## 2. Strategy: MockStudent-first, then LIVE-against-Windows-Students

The Student track's superpower was **MockTeacher** — a headless stand-in that let us build and
prove each subsystem structurally (`--selftest`, `--streamtest`, `--cameratest`, `--audiotest`,
`--locktest`, `--inputtest`) *before* the borrowed-Windows LIVE runs. The Teacher track needs the
symmetric tool **first**:

- **TT-0 MockStudent** — a student stand-in that connects to the Mac Teacher's server, sends
  `Hello`, answers `Ping`, ACKs commands, and *emits* synthetic student data (screen MJPEG +
  H.264, camera JPEG, PCM talkback, chat, mic/webcam state). Run **N instances** to simulate a
  classroom on one Mac. This is the harness backbone for every later phase's structural proof.

Then each phase gets **two** proofs, exactly as the Student track did:
1. **Structural** (headless, deterministic): drive it with MockStudent instances on loopback.
2. **LIVE**: one or more **real shipped Windows Students** connect to the Mac Teacher.

---

## 3. What is ALREADY built (reuse inventory — Student track M17–M22)

The Teacher is *not* a from-scratch native effort. Most of its native surface already exists in
`native/NtyCapture/libNtyCapture.dylib`, built and LIVE-proven during the Student track:

| Native capability | Built in | Teacher reuse |
|---|---|---|
| ScreenCaptureKit screen capture (BGRA + fitted JPEG) | M17 | **Teacher "Share My Screen"** broadcast source |
| H.264 **encode** via VideoToolbox (Annex-B, Baseline/CBR) | M18 | **Teacher screen broadcast** in H.264 |
| AVCaptureSession camera → JPEG (peer-cam 320×240) | M19 | **Teacher camera** in Conference |
| AVAudioEngine mic **capture** → PCM 16k mono | M20 | **Teacher audio broadcast** + mic |
| AVAudioEngine **playback** + jitter buffer (single stream) | M20 | basis for **student-audio mixing** |
| Kiosk shield + presentationOptions + dead-man (Student-only) | M21 | n/a (Teacher isn't locked) |
| CGEventTap input guard (Student-only) | M22 | n/a |

**The one genuinely-new native piece the Teacher needs is H.264 DECODE** (VTDecompressionSession)
— symmetric to the M18 encoder, so VideoToolbox is already proven viable. See §5.

---

## 4. Portable-as-is vs rewrite surface (shipped Windows Teacher map)

**Portable C# — copy with near-zero change** (verified platform-neutral in the map):
- `src/ClassroomCtrl.Shared/Protocol/*` — `Envelope`, `MessageType`, `Messages`, `Constants`
  (already vendored into `ClassroomCtrl.Shared.Wire` on the Avalonia side).
- `src/ClassroomCtrl.Networking/TcpControlServer.cs` — `TcpListener(IPAddress.Any, 7777)`,
  4-byte BE length + MessagePack framing, per-peer prioritized channels (reliable/audio/voice/
  input/lossy-video), `WriterLoopAsync`, Ping/Pong auto-reply, 15 s stale-sweep. **Drop `Microsoft.Win32.Registry`** (used for `TeacherIPConfig`) → NSUserDefaults.
- `src/ClassroomCtrl.Teacher/Services/ControlServer.cs` (~1883 lines) — roster, breakout maps,
  the ~40 `Broadcast*/…OneAsync` send methods, and the `OnMessage` inbound `switch`. Mostly MVVM-
  neutral; the send-sites are pure wire.
- `src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs` + conference VMs — mostly portable MVVM
  (already using CommunityToolkit.Mvvm).

**Rewrite surface** (concentrated, per the map):
- **All `.xaml` → `.axaml`** (main window, student grid, `StudentCard`, bulk toolbar, conference
  views, dialogs). ~90% syntax overlap (Student-track experience).
- **Codec:** `Shared/Codec/H264DecoderWrapper.cs` (OpenH264/H264Sharp) → **VideoToolbox decode**
  in the dylib. (Encoder already done.)
- **Capture:** `Shared/Capture/DxgiScreenCapturer.cs` → **ScreenCaptureKit** (already done).
- **Audio:** `Services/StudentAudioMixer.cs` (NAudio `WaveOutEvent` mixer) → **AVFoundation
  multi-stream mix**; `Services/AudioBroadcaster.cs` (NAudio `WaveInEvent` + `WasapiLoopbackCapture`)
  → AVFoundation (mic done; **system-loopback is the known gap**, §6).
- **Webcam:** `Services/CameraBroadcastService.cs` (AForge DirectShow) → AVFoundation (done).
- **Platform glue:** Registry→NSUserDefaults, `%LOCALAPPDATA%`→`~/Library/Application Support`,
  `System.Media.SoundPlayer`→NSSound, `netsh` firewall→drop, DPI P/Invoke→drop (Avalonia handles).

---

## 5. The phases (TT-0 … TT-13)

Rough sizes are **loose relative** (S ≈ 1–2 days, M ≈ 3–5 days, L ≈ 1–2 weeks at the team's
proven velocity), not commitments. Ordered so each builds on the last and each has a real LIVE
gate. **Bold = on the critical path to a minimally-useful Mac Teacher** (roster + see screens +
lock/policy + bulk).

| Phase | Goal | New native? | Size | LIVE gate (vs shipped Windows Student) |
|---|---|---|---|---|
| **TT-0** | **MockStudent harness tool** | no | **M** | n/a (enables all later structural proofs) |
| **TT-1** | **Server + roster + Hello/Ping/Pong** | no | **M** | Windows Student connects → appears in roster; liveness + stale-sweep |
| **TT-2** | **Student grid UI (tiles)** | no | **M** | Windows Students show as live tiles; join/leave updates |
| **TT-3** | **Receive + display student screens — MJPEG** | no | **M** | Request a Windows Student's screen → see it live (MJPEG) |
| **TT-4 ✅** | **H.264 student-screen DECODE (VTDecompressionSession)** | **YES** | **M** (done) | ✅ LIVE — Windows (OpenH264) AND Mac (VideoToolbox) students → decoded + displayed |
| **TT-5 ✅** | **Core commands: lock/unlock + power** (policy deferred) | no | **M** (done) | ✅ LIVE — Mac Teacher locks a Mac Student (M21 kiosk) + locks/logs-off a Windows Student; power platform-gated |
| **TT-6** | **Multi-select + bulk actions (v1.2)** | no | **M** | Select N Windows Students → bulk lock/policy/mute/file/power |
| TT-7 | Chat + notifications + hand-raise + reactions | no | M | Two-way chat; hand-raise/reaction surfaces on the Mac Teacher |
| TT-8 | Teacher screen broadcast ("Share My Screen") | no (reuse M17/M18) | M | Mac Teacher shares screen → Windows Students display it |
| TT-9 | Student audio talkback — multi-student mixing | extend M20 | M | Hear multiple Windows Students' mics mixed on the Mac Teacher |
| TT-10 | Teacher audio broadcast + mic-monitor | reuse M20 | M | Windows Students hear the Mac Teacher's mic; monitor one student |
| TT-11 | Camera + Conference mode (star relay) | reuse M19 | L | Conference with Windows Students: teacher+student cams + share |
| TT-12 | File distribution | no | S | Send a file to Windows Students; they receive it |
| TT-13 | System integration + packaging | no | M | Signed `.app`, permissions, settings persist across launches |

### Per-phase detail

**TT-0 — MockStudent tool.** New `tools/MockStudent` (mirror of `tools/MockTeacher`). Connects to
the Teacher on 7777, sends `HelloMessage`, pings every 5 s, and on command emits: synthetic
`StudentStreamFrame 0x0326` (MJPEG solid-color + H.264 via the M18 encoder path), `ConferenceCameraFrame`,
`StudentAudioStreamFrame 0x032C` (PCM tone), `ChatMessage`, `MicStateUpdate`/`WebcamStateUpdate`.
Flags: `--students N` (spawn a fake classroom), `--selftest` (drive the Teacher's server loop and
assert). *Deliverable: the structural backbone for TT-1…TT-12.*

**TT-1 — Server + roster.** Port `TcpControlServer.cs` + the roster half of `ControlServer.cs`.
Mac Teacher listens on 7777; `Hello`→`StudentJoined`; Ping/Pong auto-reply; 15 s stale-sweep →
`PeerDisconnected`. *Structural:* MockStudent ×5 connect/disconnect. *LIVE:* a real Windows
Student's `Hello` populates the roster; pull its network cable → sweeps out after 15 s.

**TT-2 — Student grid.** Port `MainWindow.xaml` grid + `Controls/StudentCard.xaml` → AXAML; bind
`ObservableCollection<StudentViewModel>`. *LIVE:* Windows Students appear as tiles with name/status;
live join/leave.

**TT-3 — Student screens, MJPEG.** Wire `RequestStudentStreamAsync`/`StopStudentStreamAsync`
(`StudentStreamStart 0x0325`/`Stop 0x0327`); receive `StudentStreamFrame 0x0326` where
`ScreenStreamFrameMessage.Codec == Mjpeg`; decode with Avalonia `Bitmap`/SkiaSharp (replaces WPF
`BitmapImage`); render in tile thumbnails + a full-screen `StudentScreenWindow`. *LIVE:* request a
Windows Student's screen (set the student to MJPEG) → live thumbnail + full-screen.

**TT-4 — H.264 student-screen DECODE ✅ COMPLETE + LIVE (2026-07-14).** Landed exactly as scoped:
`native/NtyCapture/Sources/H264Decoder.swift` (VTDecompressionSession, handle-based ABI — the first
multi-instance native subsystem) + the managed `H264DecoderWrapper` (§20) wired into the `RenderFrame`
H264 branch. The bitstream risk was retired (one uniform Annex-B Baseline shape from every student;
`profile_idc=66` confirmed in BELL's SPS). Proven headless (native round-trip 13/13; `TT4CGate` 18/18
incl. a committed real-OpenH264 BELL fixture) AND LIVE (Windows OpenH264 + Mac VideoToolbox students).
Measured detail: BELL emits 4-byte start codes only (the 3-byte parse stays proven synthetically).
Undecodable H.264 → visible MJPEG fallback. See `docs/TT-4-FINDINGS.md`. *(Original plan below.)*

**TT-4 (original plan) — the one big new native piece — now ADDITIVE.** As of TT-3 the
whole pipeline is proven and LIVE (request → receive → decode → Image → stop, MJPEG), and the render is
a **codec-dispatch `RenderFrame` with an H.264 stub already in place** (TT-3-C). So TT-4 fills **only**
the `VideoCodec.H264` branch + the native decoder: add to the dylib `nty_h264_decode_start/feed/stop` —
Annex-B NAL → `VTDecompressionSession` → CVPixelBuffer (BGRA) callback (GC-rooted, §20 template) →
`WriteableBitmap.Lock()` + `Marshal.Copy` → return a fresh `WriteableBitmap` (verified assignable to the
`Bitmap? CurrentFrame` target — `WriteableBitmap : Bitmap`). Replaces `Shared/Codec/H264DecoderWrapper.cs`
(OpenH264). The subscription, studentId filter, request/stop lifecycle, and UI-thread marshal are
**untouched**. **Scale is a phantom** (§7): streams are on-demand, 1–4 concurrent, so there is **no
×40-decode requirement** and **no downscale/decode-on-demand mitigation needed**. The **remaining** risk
is narrow and technical — does VideoToolbox decode the shipped student's **OpenH264-Baseline** SPS/PPS/NAL
shape? Investigate that first (the M18 encoder findings are the template). *Structural:* MockStudent
streams H.264 (loopback encode→decode round-trip). *LIVE:* a Windows Student in H.264 mode → decoded on
the Mac Teacher. `--classroom 40` stays a **stress test, not a gate**.

**TT-5 — Core commands ✅ COMPLETE + LIVE (2026-07-14).** Shipped **lock/unlock + power**
(logoff/restart/shutdown); **policy deferred** (needs an editor dialog AND macOS enforcement, which
doesn't exist — a reflect-only badge isn't a feature). `StudentCommandController` + `IStudentCommandSink`
(the command-side analog of TT-3's `ScreenViewController`/`IStudentStreamSource`) forward to the
already-ported `ControlServer.LockOneAsync`/`PowerOneAsync` — **every command `reliable:true`**, fixing
the shipped v1.2.1-class latent bug where per-student commands defaulted to the lossy queue. Power is
**platform-gated** (`StudentPlatform.CanReceivePower` from `HelloMessage.OsVersion` — already on the
wire, zero Shared.Wire change): enabled for Windows students, disabled-with-tooltip for Mac students
(no macOS power handler yet). Confirm dialog for power (Cancel = default, safer than shipped Yes).
**Full-circle interop proven:** a macOS Teacher sends the exact messages the macOS Student already
receives (M15/M21) — a Mac Student is hard-locked (M21 kiosk), a Windows Student locks + logs off.
Gates: TT5Gate 23/23 (reliable channel + platform gate + confirm) · `--teacherselftest` 22/22 (+2
delivery checks) · T1-T27. See `docs/TT-5-FINDINGS.md`. *(Original plan below.)*

**TT-5 (original plan) — Core commands.** Port the send-sites: `BroadcastLockAsync`/`LockOneAsync`
(`0x0300/0x0301`), `BroadcastPolicyAsync`/`ApplyPolicyToOneAsync`/revert (`0x0400/0x0401`),
`BroadcastPowerAsync` (`ForceShutdown/Restart/Logoff 0x0302-0x0304`), with confirm dialogs.
**Full-circle interop:** these are the exact messages the *macOS Student* already receives (M15/M21) —
now a macOS Teacher sends them. *LIVE:* Mac Teacher ⇄ Windows Student for each.

**TT-6 — Multi-select + bulk (v1.2).** Port `_selectedTileIds`, `HasSelection`, Shift-range anchor,
`SelectAll`/`ClearSelection`, the `Bulk*` `[RelayCommand]`s (`MainViewModel.cs:3777-4024`), and the
floating `BulkToolbar` (`MainWindow.xaml:803-905`) → AXAML with slide-in. *LIVE:* select N Windows
Students → bulk lock/policy/mute/file/power with "Sending i of N…" progress.

**TT-7 — Chat + notifications.** Chat rail + `Views/NotificationOverlay` + `Services/SoundService`
(`System.Media.SoundPlayer` → NSSound/AVAudioPlayer). Handlers: chat, hand-raise, reaction,
mic/webcam-state. *LIVE:* two-way chat; a Windows Student's hand-raise/reaction pops on the Mac.

**TT-8 — Teacher "Share My Screen".** Reuse M17 capture + M18 H.264 encode; wire
`BroadcastScreenStreamControlAsync`/`BroadcastScreenFrameAsync` (`ScreenStreamStart/Frame/Stop
0x0322-0x0324`). Needs **Screen Recording** TCC. *LIVE:* Mac Teacher shares → Windows Students
display it (they already decode).

**TT-9 — Student audio mixing.** Extend the M20 single-stream playback to a **multi-student mixer**
(replaces NAudio `MixingSampleProvider` in `StudentAudioMixer.cs`): per-student jitter buffer →
one AVAudioEngine mix bus. Receive `StudentAudioStreamFrame 0x032C`. *LIVE:* two Windows Students
talk → both audible, mixed.

**TT-10 — Teacher audio broadcast + mic-monitor.** Reuse M20 mic capture; wire
`BroadcastAudioStreamControlAsync`/`FrameAsync` (`0x0328-0x032A`), `SendMicMonitorStart/Stop`
(`0x0490/0x0491`), `ForceMuteStudentMic 0x032E`. **"Share Computer Audio" (system loopback) is the
gap (§6).** *LIVE:* Windows Students hear the Mac Teacher's mic; monitor one student.

**TT-11 — Camera + Conference.** Reuse M19 camera. Port the **star-topology relay** in
`ControlServer` (peer cam `ConferenceCamera* 0x0680-0x0682`, share `ConferenceShare* 0x0683-0x0685`,
request/response `0x0686/0x0687`) — portable C#. Port `Shared.Wpf/Conference/*` (gallery, tile,
share, sidebar, toolbar) + VMs → Avalonia. *LIVE:* conference with Windows Students — teacher +
student cams + a shared screen in the gallery.

**TT-12 — File distribution.** `BroadcastFileAsync` (`FileAnnounce/Chunk/Complete 0x0200-0x0203`)
over the reliable channel. Storage `%LOCALAPPDATA%`→`~/Library`. *LIVE:* push a file to Windows
Students; they receive + open.

**TT-13 — System integration.** NSUserDefaults (Language, codec/capture prefs — Registry
replacement), storage paths, `.app` packaging + signing (extend `scripts/package-app.sh`),
permission bundle (**Screen Recording** for broadcast, **Camera**, **Microphone**; Accessibility
NOT needed for the Teacher), LaunchAgent if auto-start is wanted. *LIVE:* fresh signed `.app`,
grants persist across relaunch, full session with Windows Students.

---

## 6. Genuinely-new native pieces (everything else is reuse)

1. **H.264 DECODE — VTDecompressionSession (TT-4).** The one genuinely-new native piece; symmetric to
   the proven M18 encoder. As of TT-3 it is **additive** — the receive/decode/render/stop pipeline is
   LIVE and the codec-dispatch render seam already has the H.264 slot (TT-3-C), so TT-4 = fill that
   branch + the decoder. **Scale is NOT a factor** (streams are on-demand, 1–4 concurrent — §7). The
   remaining risk is narrow: decoding the shipped student's **OpenH264-Baseline** SPS/PPS/NAL shape.
   Investigate that first (the M18 findings are the template).
2. **Multi-student audio mix (TT-9).** Extends M20 playback from one stream to a mixed bus with
   per-student jitter buffers. Medium risk (mixing + drift, not new frameworks).
3. **System-audio loopback capture (TT-10, "Share Computer Audio") — the real gap.** Windows uses
   `WasapiLoopbackCapture`; macOS has **no direct equivalent**. Options: ScreenCaptureKit audio
   capture (macOS 13+), an installed virtual audio device (e.g. a BlackHole-style aggregate), or
   **defer** (teacher-mic broadcast in TT-10 works without it). Flag as its own investigation.

Everything else (screen capture, H.264 encode, camera, mic capture, single-stream playback) is
**already built and LIVE-proven** in M17–M22.

---

## 7. Known gaps / risks / investigations to schedule

- **TT-4 H.264 decode** — highest technical risk. Investigation sub-phase before implementation.
- **TT-10 system-audio loopback** — no clean macOS equivalent; decide ScreenCaptureKit-audio vs
  virtual-device vs defer.
- **`ClassroomCtrl.Shared` is not platform-clean on Windows** (it sets `UseWPF=true` and pulls
  H264Sharp/Vortice). The Avalonia side already vendors a clean `Shared.Wire`; when porting
  `ControlServer`/VMs, pull only the protocol/model types, not the WPF/codec deps.
- **Scale/perf — RESOLVED (gate CLOSED, not deferred; TT-3, 2026-07-14).** Student screen streams are
  **on-demand**, not always-on: traced from the shipped code (`ViewStudentScreen` → targeted
  `StudentStreamStart` for ONE student; no all-student loop; tiles never stream — thumbnails come only
  from a manual screenshot), and confirmed with sales that customer B expects the shipped
  one-at-a-time behavior, **not** a live thumbnail wall. So the Teacher decodes **1–4 concurrent**
  streams, **never 40** — the "×40 tiles decoding" perf pass (downscale thumbnails / decode-on-demand)
  is **unnecessary and is dropped**. `--classroom 40` (MockStudent) stays as a **stress test, not a
  gate**. *If* a live-thumbnail-wall feature is ever requested, it is a NEW feature that would
  reintroduce this gate — a scoped request with known cost, not a bug. See `docs/TT-3-FINDINGS.md`.
- **Deferred/optional features** (not on the shippable-Teacher path; port later if wanted): remote
  control (`0x0480-0x0486`), demo/annotation/screen-pen (`0x0440-0x0452`), net movie
  (`0x0470-0x0473`), recording (NReco/ffmpeg → AVAssetWriter), breakout rooms (`0x0600-0x0625`),
  exam/quiz + charts + Excel/Word export (`0x0700-0x0703`), UDP discovery beacon (7778).

---

## 8. Constraints (inherited, non-negotiable)

- Shipped Windows repo **READ-ONLY** — copy patterns from it, never modify it.
- Wire protocol **T1–T27 frozen**; MessagePack `Envelope` byte-compatible with Windows Students.
- New work stays in the Avalonia repo (`src/…Teacher` project to be created, `native/`,
  `tools/MockStudent`). MessagePack pinned **2.5.187** (NU1902/NU1903 expected).
- Per-phase commits; structural harness (MockStudent) **and** LIVE (real Windows Students) before
  close-out; investigation-first for TT-4 and the TT-10 loopback question.

---

## 9. Suggested first three moves for the shift picking this up

1. **TT-0 MockStudent** — build the harness first; everything else leans on it.
2. **TT-1 server + roster** — smallest real milestone; proves a Windows Student can see a Mac
   Teacher at all (the foundational interop direction).
3. **TT-3 → TT-4 screens** — the demo-defining capability (a teacher *seeing* students); do MJPEG
   first for a fast LIVE win, then invest in the H.264 decode.

This gets to a **minimally-useful Mac Teacher** (roster + see screens + lock/policy + bulk) by
**TT-6**, with conference/audio/broadcast/files layered after.
