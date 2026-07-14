# Student Track Roadmap V2 — the Mac Student is NOT done (re-scope 2026-07-14)

**Companion to `docs/TEACHER-TRACK-ROADMAP.md` §0 (re-scope).** Read that first for the wire
findings and the revised TT numbering; this doc is the Student-side mirror.

> 🔴 **The headline honesty fix:** the Student track was declared **COMPLETE (~95%) at M23**. That
> number was true for the track's **original narrow scope** — a base Mac Student that talks to a
> *shipped Windows Teacher* (connect, stream its own screen, camera, mic, audio playback, lock,
> chat-receive, hand-raise). Against the **re-scoped customer-B feature set** (Mac teacher + Mac
> students, all 7 features), the Student handles **none of the seven** and carries two known
> enforcement gaps. **Honest completion against the full scope: ~55%, not 95%.** The 95% was the
> real illusion — see the Teacher roadmap's system-% note.

---

## 1. What the Mac Student does TODAY (LIVE-proven, base classroom)

Single command sink: `ConnectionViewModel.Dispatch` (`src/ClassroomCtrl.Avalonia.Sandbox/ViewModels/ConnectionViewModel.cs:304`),
gated by the TT-6-D **default-deny** `StudentEnvelopeFilter.IsForMe` (`:320`). Handled cases:

| Capability | Dispatch case(s) | Real or reflect-only |
|---|---|---|
| Connect / Hello / Ping | `WireClient` (stable EndpointId, 5 s heartbeat, reconnect backoff) | REAL |
| Keepalive | `Pong` | n/a |
| **Screen lock** | `LockScreen` / `UnlockScreen` → `LockService` (kiosk shield + 4-layer dead-man + input guard) | **REAL enforcement** |
| Policy | `PolicyApply` / `PolicyRevert` → UI chips only | **Reflect-only — NO OS enforcement** |
| Chat (receive) | `ChatBroadcast` / `ChatDirect` / `ChatRoom` → show last text | display-only |
| **Hand-raise** | `HandRaise` / `HandLower` (receive) **+ outbound `RaiseHand` command** | **SEND already exists** |
| **Screen stream → teacher** | `StudentStreamStart` / `Stop` → capture→encode→`StudentStreamFrame` (MJPEG/H.264) | REAL |
| **Camera (peer-cam)** | `ConferenceStart` / `End` → camera→JPEG | REAL |
| **Mic talkback** | `MicMonitorStart` / `Stop` → mic→PCM→`StudentAudioStream*` | REAL |
| **Teacher-audio playback** | `AudioStreamStart` / `Stop` / `Frame` → jitter-buffered speaker playback | REAL |

**Native (one dylib, `native/NtyCapture/`):** screen capture (ScreenCaptureKit, singleton),
H.264 encode (VideoToolbox, singleton), **H.264 decode (handle-based — teacher-facing, unused by
student)**, camera (AVCaptureSession, singleton), mic capture + playback (AVAudioEngine, singletons),
lock shield (CGShieldingWindow, no TCC), input guard (CGEventTap, Accessibility best-effort).

**TCC today:** NOT sandboxed, no entitlements. Info.plist declares **Screen Recording, Camera,
Microphone, Local Network**, `LSUIElement`. Accessibility has no plist key (gated by `AXIsProcessTrusted`).
Onboarding (`PermissionsWindow`, Phase 32-D) walks **4 rows**: Screen Recording (Required),
Camera / Mic / Accessibility (Optional). **Local Network is declared but NOT in the walkthrough** —
it relies on the OS first-connect prompt (gap; see §4).

---

## 2. What's MISSING — 11 Student-side items, each gates a TT phase

Effort: S ≈ 1–2 d, M ≈ 3–5 d, L ≈ 1–2 wk (loose). "New native" = new Swift/framework surface.

| # | Item | What the Student must gain | New native | TCC | Effort·Risk | Gates |
|---|---|---|---|---|---|---|
| 1 | **Power execution** | logoff/restart/shutdown handler (no-op today) | 🔴 NSWorkspace / `osascript` | **Automation (Apple Events)** — NEW, *or* admin | M · MED | (Teacher power-gate; not one of the 7) |
| 2 | ~~Policy enforcement~~ | **OUT OF SCOPE — Windows-only** (§6) | — | — | ❌ **removed — macOS can't (no MDM)** | Teacher UI: Mac = visibly unavailable |
| 3 ✅ | **Chat SEND** | text input → `ChatBroadcast` (receive exists) | no | none | S · LOW | **DONE — TT-7 (LIVE 2026-07-14)** |
| 4 ✅ | **Reaction SEND** | emoji picker → `Reaction` 0x0674 | no | none | S · LOW | **DONE — TT-7** |
| — ✅ | *(Hand-raise SEND)* | *already existed (`RaiseHand`)* | — | — | done | TT-7 |
| 5 ✅ | **Teacher-screen RECEIVE + display** | Start/Frame/Stop + shared `ScreenFrameDecoder` (Media) + takeover viewer | reuse decoder | **none** (decode needs no Screen Recording) | M · MED | **DONE — TT-8 (LIVE 2026-07-14)** |
| 6 | **Remote-control INJECTION** | `CGEventPost` mouse/keyboard + VK→CGKeyCode map | 🔴 CGEvent inject | **Accessibility — already onboarded (best-effort), ELEVATED to required** | M–L · MED–HIGH | TT-14 |
| 7 | **Demonstration source + display** | send own screen as demo + display a peer's demo | reuse capture + decode | Screen Recording (already onboarded) | M · MED | TT-15 |
| 8 | **Net-movie playback** | AVPlayer local play + sync-to-teacher-clock | 🔴 AVPlayer | none | M · MED | TT-12b |
| 9 | **Recording indicator** | show "being recorded" badge on `StudentRecordingNotify` | no | none | S · LOW | TT-16 |
| 10 | **Breakout membership + group frames + room chat** | join room, receive group screen, room chat | reuse relay | none (reuse) | **L · HIGH** | TT-17 |
| 11 | **Quiz-taking UI** | locked quiz UI, submit answers | no (needs `Exam.Shared`) | none | **L · HIGH** | TT-18 |

**Multi-peer / ≥2-student LIVE required (TT-6-D standing rule):** #7 demonstration, #10 breakout.

---

## 3. TCC — the honest picture (with a correction to the assumed "new Input Monitoring prompt")

**Already onboarded (reuse for most items):** Screen Recording, Camera, Microphone, Accessibility
(best-effort today), Local Network.

**Genuinely NEW permission surface across the 11 items — only two:**
1. **Automation (Apple Events)** — *if* power execution (#1) uses `NSAppleScript`/`osascript` to
   log out / restart / shut down. That is a new prompt. (Alternative: privileged helper — heavier.)
2. **Accessibility ELEVATION, not a new prompt** — ⚠️ correction: there is **no separate "Input
   Monitoring" grant today**. The existing input *guard* already uses `CGEventTap` under
   **Accessibility** (best-effort, prompted lazily on first lock). Remote-control **injection**
   (`CGEventPost`, #6) uses the **same Accessibility grant** — so it is **not a new permission
   type**; it **elevates Accessibility from optional/best-effort to REQUIRED** for remote control.

Everything else (teacher-screen display, demonstration, movie, breakout, quiz, chat/reaction send,
recording indicator) needs **no new TCC** — decoding needs no Screen Recording, playback/UI need
nothing. Policy enforcement (#2) is the outlier: real enforcement is MDM/config-profile class, a
different mechanism entirely, and may be **partial-only** on macOS without an MDM.

---

## 4. Onboarding (32-D `PermissionsWindow`) — extensions needed

- **Add Local Network to the walkthrough.** Declared in Info.plist but not walked; customer B is an
  all-Mac LAN, so first-connect must not depend on catching the OS prompt. (Existing gap.)
- **Re-frame Accessibility optional → required** when remote control (TT-14) ships.
- **Add an Automation row** if power execution (TT-14-adjacent / item #1) ships via Apple Events.

---

## 5. Honest Student-track completion

- **Base classroom student (original scope): ~done** — the hard foundational native (capture,
  encode, decode-reuse, camera, mic, playback, lock) is built and LIVE-proven.
- **Against full customer-B scope: ~60%** (was ~55% at re-scope). **Batch 1 (2026-07-14) completed
  chat-send (#3), reaction-send (#4), and teacher-screen RECEIVE+display (#5 — the TT-8-A "item
  #10").** Remaining = **7 buildable items** (policy carved out as Windows-only, §6), incl. two
  **L·HIGH** (breakout, quiz) and the new-native ones (remote inject, AVPlayer movie; capture-to-file
  is teacher-side). PLUS, with TT-11 conference: a **student-side voice mixer** for peer audio
  (Answer 1 — mix N-1 peers, skip own; rides `VoiceAudioFrame` 0x0640, **no new wire**). The base
  half is the bigger *effort* chunk; the remaining half is the broader *feature* surface.
- **The "95%" was measuring the wrong denominator.** Recorded so it isn't quoted again.

---

## 6. ✅ RESOLVED (2026-07-14) — policy enforcement is WINDOWS-ONLY (customer B has no MDM)

**Decision (from sales): customer B's Macs are NOT MDM-enrolled → macOS policy enforcement is
unavailable → policy is Windows-only.** Item #2 is **removed as a task** and recorded as a **KNOWN
LIMITATION**, not a RISK. Real enforcement of USB / printing / app / site blocking on macOS is **not
achievable by an ordinary app** — it needs MDM enrollment or installed configuration profiles, which
customer B does not have.

**KNOWN LIMITATION (sales must state to customer B early, before deployment):** *USB / optical /
printing / app blocking is **Windows-only**. macOS students cannot be policy-enforced without MDM
enrollment. This is a real cross-platform capability gap.*

**Teacher-side policy UI (when eventually built):** a Mac student's policy action is a **NO-OP by
platform** → show it **visibly unavailable** (greyed, like the TT-5 power actions on Macs), never
silently ignored — so the teacher knows it won't take effect on that student.

**Why (for the record) — macOS mechanisms, none of which work without MDM:**
| Restriction | macOS mechanism | Without MDM? |
|---|---|---|
| USB / external media | config-profile / MDM `Restrictions`; USB Restricted Mode | ❌ no app API |
| Printing | MDM / config-profile print restriction | ❌ no app-level block |
| App launching | Screen Time / `com.apple.applicationaccess` | ❌ MDM/config-profile only |
| Web / site filtering | content-filter Network Extension or MDM | ⚠️ NE heavy (entitlement + approval) |

**Net effect on scope:** this REMOVES a hard MDM-class item — the Student track's remaining buildable
work shrinks by one (policy is now a documented limitation, not a phase).
