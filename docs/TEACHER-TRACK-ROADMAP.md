# Teacher Track Roadmap — macOS ClassroomCtrl Teacher (Avalonia)

> 🔴 **RE-SCOPE 2026-07-14 — READ §0 (top of file) FIRST.** Customer B (Mac teacher + Mac students,
> 50 seats) wants ALL 7 previously-"deferred" features. **NO new wire needed** (except restoring
> `Exam.Shared`). Critical path **CORRECTED** — the conference is *silent* without audio, so TT-9 +
> TT-10 move onto it. Honest completion (**TT-0…TT-10-C done + LIVE 2026-07-14**): **Teacher ~55% · Student ~60% · System ~49%.** 🔴 **FINAL Mac session 2026-07-14 — see `PROJECT-HANDOVER.md` (no more Mac access after today).** 🎉 **LIVE-proven: Mac Teacher shares video (picture+sound) to an UNMODIFIED shipped Windows student, no wire change.** System-audio ✅ first-party; camera view NOT built (relay-entangled — `TT-CAMERA-VIEW-SCOPE.md`).
> Companion: `docs/STUDENT-TRACK-ROADMAP-V2.md`. The §5 phase list below is **superseded by §0**
> where they conflict (§5 kept for the TT-0…TT-6 history).

**Status:** TT-0…TT-6 COMPLETE + LIVE-confirmed (2026-07-14) — MockStudent harness · Teacher.Core
(transport + router + roster) · windowed Teacher (live student grid) · per-student **live screen view
(MJPEG + H.264 decode)**, proven against BOTH a shipped, unmodified Windows Student (OpenH264→VT
interop) and a Mac Student (our M18 VideoToolbox H.264 — customer B's path) · **core commands
(lock/unlock + power)** — a Mac Student is hard-locked (M21 kiosk), a Windows Student locks + logs off,
power platform-gated, every command on the reliable channel. **Both biggest risks are now retired:**
the SCALE gate (TT-3 — screens are on-demand, 1–4 concurrent, not a 40-tile wall, §7) and the BITSTREAM
(TT-4 — one uniform Annex-B Baseline shape from every student, §5/§6). What remains (TT-7…TT-13) is
**known work with no research risk**, except the TT-10 system-audio-loopback investigation (§6/§7).
**TT-7 next** (chat + notifications + hand-raise + reactions). The Mac Teacher can now **see AND
command** students (multi-select + bulk incl.). Hand-off doc for a parallel shift.

> 🔴 **STANDING LIVE-GATE RULE (added TT-6-D):** any per-student command / per-student routing
> feature MUST be LIVE-tested with **≥2 students connected, one of them not the target** — one
> student cannot distinguish a targeted send from a broadcast. TT-6-D found a wrong-blast-radius
> bug (missing Student `IsForMe` filter) that had been latent since TT-3 precisely because every
> prior LIVE ran with a single Mac student. **Still single-student-blind, on the roadmap now:**
> the multi-peer *topology* features — **Conference/peer-camera relay (M19)** (highest; wants a
> dedicated 2-Mac-student re-test), **Student Demonstration**, and the future **TT-9 audio mixing**
> + **breakout/group routing** (`TargetGroupId`) — must be built + LIVE-tested with ≥2 students.
> The targeted-*command* class (lock/power/policy/DM/screen-stream/mic) is CLOSED by the TT-6-D
> filter guard. See `docs/TT-6-FINDINGS.md`.
**Author context:** drafted 2026-07-13 after Phase 31-B (Student track), grounded in a
structural map of the shipped Windows Teacher (`/Users/fewfee/Dev/nty-classroom-macos`,
v1.2.1, .NET 10 / WPF). **That shipped repo is READ-ONLY — copy from it, never modify it.**

---

## 0. RE-SCOPE (2026-07-14) — customer B wants all 7 "deferred" features · AUTHORITATIVE

**Trigger:** the team confirmed customer B (Mac teacher + Mac students, 50 seats) wants ALL SEVEN
features filed as "deferred/optional" in §7: (1) remote control, (2) demonstration/annotation,
(3) net movie, (4) recording, (5) breakout rooms, (6) exam/quiz, (7) UDP discovery. Read-only
investigation done. This section is the authoritative revised plan; §5 is kept for TT-0…TT-6 history.

### 0.1 Two headline findings
1. **🟢 NO NEW WIRE for any of the seven.** Every feature's MessageType tags AND payload POCOs are
   already vendored in the frozen `Shared.Wire` (Phase 24.1) — verified. Thirteen sessions of
   byte-stability hold. **One exception: exam/quiz needs `ClassroomCtrl.Exam.Shared` restored**
   (the `Quiz*` tags exist; the payload POCOs were `#if false`'d out in TT-1-C). UDP discovery is
   not TCP wire at all (a separate UDP beacon on 7778, `UdpClient` — cross-platform, near-direct port).
2. **The work MOVED to Student + native + UI.** `Teacher.Core` (`ControlServer`) is already ported
   for most send-paths (remote-control send, movie broadcast, breakout group-state, demo state,
   recording-notify). **The Mac Student (Sandbox) handles NONE of the seven** — see
   `docs/STUDENT-TRACK-ROADMAP-V2.md`. Remaining weight = Student handlers + new native (CGEvent
   inject, AVPlayer, capture-to-file, UdpClient) + UI.

### 0.2 🔴 Q1 CORRECTION — the conference is SILENT without audio (critical path grew)
Traced in the shipped product: **the conference star-relay carries VIDEO ONLY** — it fans out
exactly screen-share (`ControlServer.cs:1579`), webcam (`:1634`), and reactions (`:1516`). There is
**no `ConferenceAudio*` message type**; audio is a physically separate wire path on separate
channels (`_audioOutbox`/`_voiceOutbox` vs the video `_outbox`). Consequences:
- **TT-11 alone = a silent conference.** An audible conference REQUIRES **TT-9** (teacher hears
  students, `StudentAudioStream` 0x032B-0x032D + `StudentAudioMixer`) **+ TT-10** (students hear
  teacher, `AudioStream` 0x0328-0x032A). Both move ONTO the critical path.
- **🚩 Product decision (flag before TT-11):** true **student↔student** audio (everyone hears
  everyone) does NOT exist in the shipped product — the conference mic button only does
  student→teacher talkback to the *teacher's local speaker*; the group-voice peer-relay (0x0640)
  was never wired to the conference (breakout-only). If customer B expects peer-to-peer conference
  audio, that is **net-new work** (a 0x0640-style relay bound to `ConferenceId`), not a port.

> **FLAG 1 RESOLVED (2026-07-14): peer audio = YES** (net-new, Zoom-style — everyone hears everyone).
> 🟢 **NO new wire** — rides the existing `VoiceAudioFrame 0x0640` (group-targeted, sender-excluded)
> + `Envelope.TargetGroupId`; the 13-session byte-stability holds. **Topology: star-relay + student-
> side voice mixer** — the teacher forwards each frame to group peers via the ported 0x0640 relay
> (just bind it to the ConferenceId; today it gates on breakout-room membership), and each student
> mixes the N-1 incoming peers, skipping its own SenderId — matches the shipped receiver-mix design.
> A **TT-11 addition, NOT a TT-9 reshape** (TT-9/TT-10 stay the shipped teacher↔student model). Open
> technical item: **acoustic echo (AEC)** — see §0.6. Still: build TT-9/TT-10 first (shipped model).

### 0.3 Revised phase table (TT-7 … TT-18)
🔴 = multi-peer topology (build + LIVE with ≥2 students); ⚠️ = milder multi-student (aggregation/broadcast).

| Phase | Goal | New native? | Multi-peer | Effort·Risk | Notes |
|---|---|---|---|---|---|
| **TT-7 ✅** | Chat + hand-raise + reactions (+ Mac-Student SEND) | no | ⚠️ aggregation | **M·LOW** | **DONE + LIVE 2026-07-14** — hand-raise send pre-existed; chat/reaction send + DMs reliable; bug #6 fixed |
| **TT-8 ✅** | Teacher "Share My Screen" | no (reuse M17/M18) | ⚠️ broadcast | **M·LOW–MED** | **DONE + LIVE 2026-07-14** — Teacher bundle (Screen Rec TCC); shared Media decode lib; item #10 done; bug #5 fixed |
| **TT-9 ✅** | Student audio mixing | extend M20 (native nty_mix_*) | 🟢 no (0x032B/C/D vendored) | **M·MED** | **DONE + LIVE 2026-07-14** — reusable N-node mixer core (TT-11 reuses it); **3 deliberate divergences** (cap w/ visible degrade · gain-norm · one core vs shipped's two mixers); kill-one-sender STALL gate; teacher-only ~10%/core at N=25 **and** N=50 (cap-bounded). See `docs/TT-9-FINDINGS.md` + `TT-9-LIVE-CONFIRMATION.md` |
| **TT-10-B ✅** | Teacher audio BROADCAST ("Talk to Class") | reuse M20 | 🟢 no | **M·LOW–MED** | **DONE + LIVE 2026-07-14** — teacher mic → all students; SHIPPED BUG #7 (audio Start/Stop lossy) fixed + clean-stop verified live. See `TT-10-BC-LIVE-CONFIRMATION.md` |
| **TT-10-C ✅** | **"Share Computer Audio"** (system audio → students) | SCK capturesAudio | 🟢 no | **M·MED** | **DONE + LIVE 2026-07-14** — SCK first-party system-audio → AudioStreamFrame 0x0329; LIVE: 868 frames, normal quality (16k-mono honored, no resampler), + Share-Screen+Audio SIMULTANEOUS to an **unmodified shipped Windows student**. See `TT-10-BC-LIVE-CONFIRMATION.md` |
| **TT-10 (rest)** | mic-monitor (done via TT-9) | reuse M20 | ✅ | — | mic-monitor done via TT-9. 🔴 **TOR 11.2.1 power-ON cannot be met on Apple Silicon** — see `TOR-COMPLIANCE.md` |
| **TT-11** | Camera + Conference (star relay) **+ peer audio** | reuse M19 + student voice mixer | 🔴 yes (flagship) | **L·HIGH** | + peer audio (rides 0x0640, **NO new wire**; star-relay + student mixer; AEC open — §0.6) |
| **TT-12a** | File distribution | no | ⚠️ broadcast | **S·LOW** | reliable channel |
| **TT-12b** | **Net movie** | 🔴 AVPlayer + sync | ⚠️ broadcast | **M·MED** | rides TT-12a transport; teacher SEND ported |
| **TT-13** | System integration + packaging **+ UDP discovery** | UdpClient + LNP entitlement | no | **M·MED** | discovery = near-direct `UdpClient` port |
| **TT-14** | **Remote control** | 🔴 CGEvent inject + Accessibility | no | **M–L·MED–HIGH** | teacher SEND ported; student inject net-new |
| **TT-15** | **Demonstration** (spotlight relay) | reuse capture + decode | 🔴 yes | **M·MED** | build with the TT-9/11 multi-peer cluster |
| **TT-16** | **Recording** | 🔴 ffmpeg-bundle / AVAssetWriter | no | **M·MED** | 🚩 decision flagged (0.6); notify ported |
| **TT-17** | **Breakout rooms** (+ room chat) | reuse relay | 🔴 yes | **L·HIGH** | activates the inert `IsForMe` `TargetGroupId` branch |
| **TT-18** | **Exam / quiz** | no (restore `Exam.Shared`) | no | **L·HIGH** | needs `Exam.Shared` POCOs restored + large UI |

→ then **UI POLISH → LICENSE/ACTIVATE KEY → P35 (sign+notarize) → SHIP** (end-game unchanged, §5).

### 0.4 Revised critical path to a customer-B-deployable build (CORRECTED for audio)
**TT-7 → TT-8 → TT-9 → TT-10 → TT-11 (now audible) → TT-12a (files) → TT-13 (discovery + packaging) → P35.**
The remaining re-scope features — **remote control (TT-14), demonstration (TT-15), recording (TT-16),
breakout (TT-17), exam/quiz (TT-18)** — are **parity features that fast-follow by customer priority**;
net movie (TT-12b) and UDP discovery (TT-13) fold into those phases. The three **L·HIGH** phases
(TT-11 conference, TT-17 breakout, TT-18 quiz) dominate the remaining effort.

### 0.5 Build order (approved 2026-07-14)
- **BATCH 1 = TT-7 + TT-8** — both LOW risk, no new native, independent; one LIVE session, per-phase
  checkpoints. NOTE: TT-8's **teacher-capture** side is LIVE-testable against Windows students
  immediately (they already decode); the **Mac-Student display** of a teacher screen is **net-new**
  (Student-track item #10) and is what customer B needs — track it explicitly.
- **TT-12 SPLIT:** **TT-12a** file distribution (LOW, no native, batchable later) / **TT-12b** net
  movie (new native — AVPlayer + sync drift — solo).

### 0.6 🚩 Decisions flagged for later (do NOT decide now)
- **TT-16 Recording — ffmpeg-bundle vs AVAssetWriter.** ffmpeg (shipped uses NReco/ffmpeg):
  cross-platform, but a large bundled binary + licensing to clear. AVAssetWriter: native, cleaner,
  but new code. A real trade-off — decide **at** TT-16, not by accident.
- ✅ **TT-11 conference audio — RESOLVED (2026-07-14): peer audio YES** (§0.2). No new wire (rides
  `VoiceAudioFrame 0x0640` + `TargetGroupId`); star-relay + student-side voice mixer. **Still open
  (technical, decide at TT-11): acoustic echo (AEC).** Exclude-self is handled at the mixer (skip own
  SenderId), but ACOUSTIC echo (a mic hearing the speaker output) needs either AVAudioEngine
  voice-processing IO (built-in AEC — a change to the M20 capture) or headphones (classroom reality).
- ✅ **Policy — RESOLVED (2026-07-14): Windows-only** (customer B has no MDM). Removed from macOS
  scope; the Teacher's policy UI (when built) shows a Mac student's policy visibly unavailable (like
  the greyed power actions). Known limitation; see Student V2 §6.

### 0.7 Honest completion (updated 2026-07-14 — after TT-9 LIVE + TT-10-B/-C build)
- **Teacher track: ~54%** — **TT-0…TT-9 done+LIVE, TT-10-B (Talk) + TT-10-C (Share Computer Audio)
  built + **LIVE** (~12 of ~19)**. Camera view NOT built (relay-entangled, scoped). TT-9 LIVE on a one-Mac rig (two
  co-located sources + headphones); cap/stall/scale proven headlessly. (Was ~37% at re-scope, ~47%
  after batch 1.)
- **Student track: ~60%** against full customer-B scope — batch 1 added chat-send + reaction-send +
  teacher-screen display (item #10). Remaining V2 items: power, policy (RISK), remote-inject, demo,
  movie, recording-indicator, breakout, quiz. (Was ~55%; NOT the 95% claimed at M23.)
- **SYSTEM: ~45%.** Wire 100%; foundational native done + LIVE; **+2 feature areas fully closed both
  sides** (chat/notifications, teacher screen-share). The hard part (multi-peer relay + new-native
  audio + quiz) is still ahead — see §0.4.

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
| **TT-6 ✅** | **Multi-select + bulk (lock/unlock + power)** | no | **M** (done) | ✅ LIVE — select N → bulk lock/unlock/power (platform-skip); **found+fixed a wrong-blast-radius Student-filter bug** |
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

**TT-6 — Multi-select + bulk ✅ COMPLETE + LIVE (2026-07-14).** Shipped **lock/unlock + power**
(policy/mic/file deferred with their features — no dead buttons). Selection logic as the UI-agnostic
`TileSelectionModel` (Teacher.Core, committed-gated); **macOS click idiom** (plain=select-one,
⌘=toggle, Shift=range, ⌘A/Esc — a deliberate divergence from shipped's plain-click-toggles). Bulk
routes through `StudentCommandController.ExecuteBulkAsync` → the reliable-channel guard covers bulk by
construction; **platform-aware bulk power** skips Mac students with a visible report; count-aware
Cancel-default confirm. Floating `BulkToolbar` (AXAML, slide-in, "Sending i of N"). **This phase's
LIVE gate found + fixed a wrong-blast-radius bug** — the port had dropped the shipped Student's
`IsForMe` receive filter (Service→single-process port), so targeted commands hit every Mac student;
fixed with `StudentEnvelopeFilter.IsForMe` (default-deny) guarding `ConnectionViewModel.Dispatch`.
Gates: `--teacherselftest` 74, `--selftest` 9. See `docs/TT-6-FINDINGS.md`. *(Original plan below.)*

**TT-6 (original plan) — Multi-select + bulk (v1.2).** Port `_selectedTileIds`, `HasSelection`,
Shift-range anchor, `SelectAll`/`ClearSelection`, the `Bulk*` `[RelayCommand]`s
(`MainViewModel.cs:3777-4024`), and the floating `BulkToolbar` (`MainWindow.xaml:803-905`) → AXAML
with slide-in. *LIVE:* select N Windows Students → bulk lock/policy/mute/file/power with "Sending i
of N…" progress.

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

### Remaining phases — risk + multi-peer at a glance (ratings 2026-07-14)

Risk = residual difficulty/uncertainty for THIS port (research + new-native + UI weight). 🔴 MULTI-PEER
= inherently multi-student **topology** (fan-out/relay/mix), so it MUST be LIVE-tested with ≥2 students
per the TT-6-D standing rule; ⚠️ = milder multi-student behavior (aggregation / broadcast-at-scale).

| Phase | Risk | Multi-peer | Why |
|---|---|---|---|
| **TT-7** Chat + notifications + hand-raise + reactions | **LOW** | ⚠️ aggregation | Portable C# handlers + chat rail + NSSound. DM targeting now filtered (TT-6-D). Hand-raise/reactions from N students → verify the *right* student's surfaces (≥2-student check). |
| **TT-8** Teacher "Share My Screen" | **LOW–MED** | ⚠️ broadcast fan-out | Reuses M17 capture + M18 encode (both LIVE). Needs Screen Recording TCC (P35 friction). One-to-many broadcast to N students unverified at scale. |
| **TT-9** Student audio mixing | **MED** | 🔴 **YES** | Extends M20 single-stream → per-student jitter buffer + one mix bus. Meaningless with one student; mixing/drift is the risk. |
| **TT-10** Teacher audio broadcast + mic-monitor | **MED–HIGH** | ⚠️ (monitor targeted) | Teacher-mic broadcast reuses M20 (low). Mic-monitor is targeted → covered by the TT-6-D filter. **"Share Computer Audio" system-loopback has no clean macOS equiv → investigation sub-phase** (§6). |
| **TT-11** Camera + Conference (star relay) | **HIGH** | 🔴 **YES (flagship)** | The star-topology relay (who sees whose cam/share) is inherently multi-peer + never multi-student-tested (subsumes the M19 re-test), **plus** the large Conference UI port (gallery/tile/share/sidebar/toolbar). Size L. |
| **TT-12** File distribution | **LOW** | ⚠️ broadcast fan-out | FileAnnounce/Chunk/Complete on the reliable channel + storage path. Portable. Size S. |
| **TT-13** System integration + packaging | **MED** | no | NSUserDefaults + `.app` packaging + permission bundle + LaunchAgent. Packaging/TCC friction; **overlaps P35** (signing/notarization). |

**Multi-peer topology phases (build + LIVE with ≥2 students from the start): TT-9, TT-11.** TT-7/TT-8/
TT-12 have milder multi-student behavior (aggregation / broadcast-at-scale) — still worth a ≥2-student
LIVE, lower risk.

### End-game ordering (confirmed 2026-07-14)

Remaining **feature** phases → **polish** → **license** → **signing** → **ship**:

1. **TT-7 … TT-13** — the remaining feature phases (this roadmap).
2. **UI POLISH** — visual parity with the shipped Windows Teacher, **keeping the macOS idioms**
   deliberately chosen (e.g. the TT-6 click model, ⌘-shortcuts, Cancel-default confirms). Not a
   pixel-copy — a native-feeling equivalent.
3. **LICENSE / ACTIVATE KEY** — offline activation key stored in config (NSUserDefaults / config.json).
   **Parked until the Teacher track is functionally complete** (per the user); do not investigate early.
4. **P35 — Developer ID signing + notarization** — eliminates the ad-hoc-rebuild TCC re-prompt
   (M22/32-F / TT-4-D friction), enables wide deployment. **TT-13 assembles the bundle; P35 signs +
   notarizes it.**
5. **SHIP.**

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
- **~~Deferred/optional features~~ → NOW SCHEDULED (re-scope 2026-07-14, §0).** All 7 are wanted by
  customer B and are slotted as TT-12b/TT-13/TT-14–18: remote control (`0x0480-0x0486`),
  demo/annotation/screen-pen (`0x0440-0x0452`), net movie (`0x0470-0x0473`), recording (NReco/ffmpeg
  → ffmpeg-bundle **or** AVAssetWriter — 🚩 §0.6), breakout rooms (`0x0600-0x0625`), exam/quiz +
  charts + Excel/Word export (`0x0700-0x0703` — needs `Exam.Shared` restored), UDP discovery beacon
  (7778). **No new wire needed for any of them** except the `Exam.Shared` POCO library. See §0.

### Cross-track follow-ups (deferred, tracked — current as of 2026-07-14)

Distinct from the optional *features* above: these are **known gaps/fixes** surfaced by the port, each
recorded so it isn't re-investigated. None block the current Teacher-track phases.

- **Windows-track follow-ups (7) — all found in the macOS port, all v1.2.x candidates; shipped repo
  untouched. Definitive list w/ file:line in `PROJECT-HANDOVER.md` §3.** ① **v1.2.1 installer** (ship);
  ② **teacher-mic `WaveInEvent`** brittleness (M20); ③ **roster namespace-gap** (peerId vs EndpointId →
  wrong-student removed at 50; found TT-1); ④ **per-student lossy-channel commands** (found TT-5-A);
  ⑤ **lossy `ScreenStreamStop`** (found TT-8-A); ⑥ **lossy `SendHandLowerAsync`** + `BroadcastReactionAsync`,
  doc says reliable (found TT-7); ⑦ **lossy `AudioStreamStart/Stop`** (dropped Stop strands every
  student's playback session open; student re-creates the player on a straggler frame → can't self-heal;
  found TT-10-B). *All fixed in the port; reported to the Windows team; we do not touch the shipped repo.*

  🔴 **RECOMMEND A SWEEP, NOT SEVEN POINT FIXES.** **FOUR of the seven (④⑤⑥⑦) are the same bug class:** a
  **control/state message riding the lossy (DropOldest) video/broadcast queue.** Dropping such a
  message leaves a client in a state the server thinks it left — a *stuck state*. v1.2.1 fixed ONE
  instance (bulk commands); the class was never swept. **The invariant to hand the Windows team:** for
  every `*Async` send site, ask *"does dropping this leave a STUCK STATE?"* — if yes, `reliable:true`;
  if it's ephemeral / self-expiring (a video frame, an expiring reaction), lossy is correct. Give them
  the invariant + the audit, not six tickets. *(①②③ are distinct classes — installer/packaging,
  mic-capture robustness, and a roster id-namespace bug — NOT the lossy-queue class; the "same class"
  is ④⑤⑥ only.)*
- **Student-track follow-ups (macOS enforcement gaps):** **macOS power execution** (Sandbox has no
  logoff/restart/shutdown handler — power is a no-op on Mac; Teacher disables it per-platform) and
  **macOS policy enforcement → RESOLVED 2026-07-14: WINDOWS-ONLY** (customer B has no MDM; macOS
  can't enforce USB/print/app without it — removed from scope, known limitation; Student V2 §6).
- **Multi-peer re-tests (from TT-6-D — single-student-blind):** **Conference/peer-camera relay (M19)**
  (highest — a dedicated 2-Mac-student re-test; subsumed into TT-11) and **Student Demonstration**
  (DemoFrame rebroadcast), if/when ported.
- **Distribution / platform validation:** **P35** (Developer ID signing + notarization — the ad-hoc-
  rebuild TCC re-prompt); **multi-display lock validation** (M21 validated single-display; multi-display
  code-correct but untested); **screenshot / per-student recording / quality-report** gaps (the deferred
  remainder of the shipped StudentScreenWindow, noted in TT-4).

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
