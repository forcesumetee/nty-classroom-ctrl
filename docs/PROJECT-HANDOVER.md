# NTY ClassroomCtrl — macOS Port · PROJECT HANDOVER
**Written 2026-07-14, the last day of Mac access.** For someone picking this up **cold, with no Mac.**
After today, macOS builds/LIVE tests STOP indefinitely (borrowed MacBook returned; no replacement yet).

> Read this first. It is the durable record — the working context does not survive the machine.
> **~48% of the full customer-B scope remains. This is not a finished project; it is a clean stop
> with maximum extracted value + an honest map of what's left.**

---

## 1. STATE — what exists and works

- **macOS STUDENT: shippable** (phases M15–M23, prior sessions) — connect, stream own screen
  (MJPEG + H.264), camera, mic talkback, teacher-audio playback, kiosk lock, chat receive, hand-raise.
- **macOS TEACHER: TT-0…TT-10-B** (this + prior sessions):
  - roster + presence · per-student screen view (MJPEG + **H.264 decode**, VideoToolbox)
  - lock/unlock · power (Windows students) · multi-select + bulk actions
  - chat + hand-raise queue + reactions + notification sounds
  - **Share My Screen** (teacher→students, ScreenCaptureKit capture)
  - **TT-9: multi-student audio mixer** — teacher hears N students mixed (native N-node AVAudioEngine,
    cap 12 + gain-norm; LIVE 2026-07-14)
  - **TT-10-B: "Talk to Class"** — teacher mic → all students (LIVE-pending user test; gate-proven)
  - **TT-10-C: "Share Computer Audio"** — teacher SYSTEM audio → all students (first-party SCK
    `capturesAudio`, no wire change; pairs with Share My Screen for video-with-sound; LIVE-pending;
    wire path gate-proven, capture proven by the probe)
- 🔴 **NOT DONE — camera view:** the teacher **cannot open a student's camera**. It is NOT built and
  is entangled with the conference relay (TT-11). Fully scoped ready-to-build in
  `docs/TT-CAMERA-VIEW-SCOPE.md`. Don't let "audio both directions is done" imply camera is done.
- **WIRE PROTOCOL: byte-perfect with shipped Windows, UNCHANGED for 14+ sessions.** T1–T27 all green.
  This is the crown jewel — cross-platform interop is real.
- **Shipped Windows repo (`nty-classroom-macos`, branch v1.2-multiselect @ 4e0467a): NEVER modified.**
- **Interop PROVEN:** unmodified shipped Windows students work against the Mac Teacher.
- **Deployment target: iMac M4 (Apple Silicon), wired Ethernet, in a school.** All our CPU numbers
  were measured on the slower borrowed M2 → **worst-case; the M4 is a tailwind** (faster, better media
  engine). Nobody should re-panic about CPU (TT-9 mixer was ~10% of one core at N=50 on M2).

## 2. BLOCKED WITHOUT A MAC (cannot build or test)
TT-10 system-audio (see the probe, §5) · **camera view** (teacher watches a student — `docs/TT-CAMERA-VIEW-SCOPE.md`; entangled with the conference relay) · **TT-11 conference — the flagship, needs ≥2 Macs** · TT-12
file + net-movie · TT-13 packaging + UDP discovery · TT-14 remote control · TT-15 demonstration ·
TT-16 recording · TT-17 breakout · TT-18 exam/quiz · **7 Student-track V2 items** · UI polish ·
license-key UI · P35 code-signing/notarization. Plus the two workarounds for 11.2.1 (§4, Finding 1).

---

## 3. 🎁 IMMEDIATE VALUE, NO MAC NEEDED — THE WINDOWS BUGS
Found by investigating the shipped code to port it. **Both customers (Windows + Mac) are exposed. The
Windows team can act on ALL of these TODAY, no Mac required.** Refs are in the SHIPPED repo
(`nty-classroom-macos`, branch v1.2-multiselect).

**THE INVARIANT (give the Windows team this one rule):** for every `*Async` send site ask *"does
dropping this leave a STUCK STATE?"* → **reliable** if yes, **lossy** if it's a hot ephemeral frame.
Bugs **#4/#5/#6/#7 are one class** (control/state on the lossy cap-16 DropOldest queue) — a **sweep**,
not four point-fixes. All four are **fixed in the macOS port** already (as reference).

| # | Bug | File:line (shipped) | Defect | Fix | Blast radius |
|---|---|---|---|---|---|
| 1 | v1.2.1 installer never built/shipped | (release process) | the v1.2.1 that half-fixes #4 never reached customers | build + ship it | both customers stuck on the buggy build |
| 2 | Teacher-mic `WaveInEvent` fragility | `src/ClassroomCtrl.Teacher/Services/AudioBroadcaster.cs:198-222` | fixed 16-bit/mono format, no negotiation; device-open failure = silent one-shot give-up; **no `RecordingStopped`/device-change handling** → unplug mid-broadcast dies silently | negotiate format; subscribe RecordingStopped; retry/surface | teacher audio silently dies, no error |
| 3 | Roster **namespace-gap** — removes the WRONG student | `src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs:2000-2011` | `OnStudentLeft` gets a **transport connection Guid**, compares it to app-level `EndpointId` (never matches), then `Students.RemoveAt(Students.Count-1)` — **deletes the LAST row regardless of who left**. The gap is acknowledged in `TcpControlServer.cs:181-187` ("Fixing … is a Tier-3 follow-up"). Same site: peerId-keyed purges of share-permission/pending/selection sets silently no-op. | map connection-Guid → EndpointId; remove by identity | **any mid-list disconnect at N>1 drops the wrong student**; the port fixed this (the "blast-radius bug" — hid 3 phases on one Mac) |
| 4 | Per-student commands route **lossy** | `src/ClassroomCtrl.Teacher/Services/ControlServer.cs:201-202` (`SendTargetedAsync`, default `reliable=false`); callers `MainViewModel.cs:1423` (lock), `:1710` (power), `StudentScreenWindow.xaml.cs:394` | LockOne/PowerOne fan out on the cap-16 DropOldest `_outbox` shared with 20 FPS video → **silently evicted under load** ("18/50 locked" bug; only bulk paths pass `reliable:true`) | route per-student control `reliable:true` | students miss lock/power during any stream burst |
| 5 | `ScreenStreamStop` routes **lossy** | `src/ClassroomCtrl.Teacher/Services/ControlServer.cs:302-308` | Stop enqueued on the same queue that's full of tail-end video → the one message that must survive is the likeliest evicted | reliable | student **stuck in full-screen viewer** |
| 6 | `SendHandLowerAsync` **lossy despite its doc** | `src/ClassroomCtrl.Teacher/Services/ControlServer.cs:655-673` (doc says "Reliable channel."; body `BroadcastAsync`). **Same doc/impl mismatch:** `BroadcastReactionAsync` `:675-685` | doc claims reliable, impl is lossy → Recognize dropped under load | reliable | student's hand stays raised (desync) |
| 7 | `AudioStreamStart/Stop` route **lossy** (NEW, found in TT-10-B) | `src/ClassroomCtrl.Teacher/Services/ControlServer.cs:790-796` | Stop on the lossy queue while frames use a separate channel → Stop evicted; **student has NO timeout and RE-CREATES the player on a straggler frame** (`MainWindow.xaml.cs:1259` `_audioPlayer ??= new AudioPlayer()`) → playback session held open indefinitely | reliable Start/Stop (done in port) | every student's audio session stuck open |
| 8? | **CANDIDATE (unverified) — conference-camera STOP** | port `ControlServer.cs:1702` relay is lossy; shipped analogous | the `ConferenceCameraStop` **relay** is lossy → stale peer camera tile (cosmetic). 🔴 **Privacy check for whoever builds camera/TT-11:** verify the SOURCE-side capture-stop (`ConferenceEnd`→`CameraStreamer.StopAsync`) is reliable end-to-end — a dropped capture-stop = a student filmed unaware. Teacher's own `CameraStop` 0x0462 IS reliable ✅. | verify + make capture-stop reliable | privacy if capture-stop is lossy (VERIFY) |

---

## 4. 🔴 WHAT THE CUSTOMER MUST BE TOLD (before deployment)

**Finding 1 — TOR 11.2.1 "เปิดเครื่อง" (power ON) CANNOT be met as written on iMac M4.**
Wake-on-LAN wakes a Mac from **sleep** only, never from full shutdown; on Apple Silicon a powered-off
Mac's NIC loses power and cannot receive a magic packet. **No network cold-boot on Apple Silicon** —
an Apple platform constraint, not our defect (the 2nd, after policy/MDM). **Shipped Windows never
implemented WoL/power-on either** (verified: no sender, no message type, no MAC capture, no TOR doc) —
so this is net-new, not a port. Two workable paths deliver what the customer wants (machines ready at
class start):
- **(a) Sleep instead of shutdown** → WoL works (magic packet + "Wake for network access"). Needs an
  Energy-Saver policy on student Macs. Teacher CAN wake them.
- **(b) Scheduled power-on** → `pmset repeat poweron MTWRF 07:30:00`. Not network-triggered, but
  machines are on before class.
- **Scope if customer accepts (a):** net-new = (i) a UDP magic-packet broadcaster on the Teacher
  (independent of TCP — target is off), (ii) the student's **MAC address**. Prefer resolving the MAC
  **teacher-side via ARP from the peer's socket IP on the wired LAN** (persist by MachineName) — this
  **avoids a `HelloMessage` wire change** (keep the 14-session wire freeze); a Hello `[Key(5)] Mac`
  add is MessagePack-backward-compatible but touches the frozen wire, so ARP is preferred. Effort:
  **S–M on wired LAN.**

**Finding 2 — 🔴 iMac M4 MAY HAVE NO ETHERNET PORT (deployment blocker — verify NOW).**
24" iMac Ethernet is in the **power adapter**: 4-port model → adapter WITH Gigabit Ethernet; **2-port
base model → adapter WITHOUT Ethernet, and it can ONLY be configured at purchase, never added later**
(only a USB-C→Ethernet dongle, which eats 1 of 2 USB-C ports). If the school bought base 2-port
iMac M4s, **50 machines have no wired networking.** Not our bug, but it lands on us if unchecked.
If they fall back to **WiFi**: client-isolation returns, and the 50-mic bandwidth math (12.8 Mbps raw
PCM) goes from "~1.3% of gigabit LAN" back to a real concern. **Verify the exact iMac SKU before
anything ships.**

**Finding 4 — ⚠️ acoustic coupling makes in-room peer-conference audio unusable as specified.**
50 iMacs with built-in mics AND speakers in **one physical room** + peer audio (everyone hears
everyone): iMac A's speaker reaches iMac B's **microphone through the air**. That's **acoustic**
coupling — **AEC (the TT-11-A spike) cannot fix cross-machine coupling in a shared room.** It would
howl. So peer conference audio is only usable if (i) it's for **remote** students, or (ii) students
wear **headsets**, or (iii) nobody thought it through. **Ask sales/deployment** — cheap now, expensive
at deployment. Affects whether TT-11 peer audio is buildable as specified.

**Also (from earlier sessions):**
- **macOS policy enforcement (USB/print/app blocking) is IMPOSSIBLE without MDM** (TOR 11.2.14).
  Customer B has no MDM → **policy is Windows-only. Tell them before deployment.**
- The **~3–5 concurrent-mic assumption (TT-9) is UNVERIFIED** with the customer.
- **Peer conference audio (Zoom-style) is wanted and IS achievable** (rides `VoiceAudioFrame` 0x0640,
  **no wire change**) — but unbuilt, and subject to Finding 4.
- **Customer B's all-Mac classroom CANNOT be delivered without a Mac to build + test on.**

## 5. TT-10 system-audio probe result (last Mac probe — see `docs/TT-10-SYSTEM-AUDIO-PROBE.md`)
**✅ RESOLVED 2026-07-14 — system audio IS capturable first-party (no third-party device), and it
COEXISTS with screen in one SCStream** (audioBuffers=200/nonSilent=41 + screenBuffers=21). Run inside
the granted Teacher bundle (TCC = the TT-8 Screen Recording grant; a bare binary has no TCC identity —
that was the earlier 0-buffers, not an API failure). So TOR 11.2.9 (record screen + audio, incl.
system audio) is **achievable first-party**. Capture is proven; the broadcast/wire build is **TT-10-C**
(reuses the TT-10-B AudioStreamFrame path + an AVAudioConverter 48k→16k step). Ask the customer whether
"เสียงของครู" = mic-only or system audio.

---

## 6. TO RESUME (needs a Mac)
- **Hardware:** **Apple Silicon (M-series), NOT Intel** (different arch; must match the target).
  **macOS 26** (matches target). **TWO Macs if at all possible** — the one-Mac constraint is *why* the
  blast-radius bug (#3) hid across 3 phases, and **TT-11 (multi-peer conference) CANNOT be validated on
  one machine.**
- **Setup:** `dotnet` (net10.0), Xcode CLT; build the dylib `native/NtyCapture/build.sh`; the bundle
  scripts (`scripts/package-*.sh`); **re-grant ALL TCC** — grants bind to the machine's ad-hoc
  signature, so a new Mac = fresh Screen Recording / Camera / Mic / Accessibility / Local-Network
  prompts; iPhone hotspot for testing (school/router WiFi blocks client isolation — on wired Ethernet
  this may not apply).
- **Fix hardcoded `/Users/fewfee/...` paths** before another user builds.
- **Next:** TT-10 (system audio — confirm the probe in a foreground app), then **TT-11** (conference +
  peer audio, reusing the TT-9 `nty_mix_*` core + the AEC spike TT-11-A — but resolve Finding 4 first).

## 7. THE PROCESS THAT WORKED (keep it)
- **Investigation-first (A-phase before build):** killed ~4 phantom risks (e.g. "peer audio needs new
  wire" — it doesn't) and surfaced **7 shipped Windows bugs.**
- **Tests must assert the DISTINGUISHING property, not the happy path** — the negative the bug would
  violate (broadcast reaches ALL; stalled source doesn't silence the class; wrong student NOT removed).
- **Per-student / multi-student features need ≥2 students in LIVE** (one can't tell targeted from
  broadcast).
- **Never touch `Shared.Wire` or the shipped repo.** 14 sessions of byte-stability = the interop
  guarantee. Protect it.
- **Per-sub-phase commits + build-verify** — every step reversible.
