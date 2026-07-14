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
| 2 | **Policy enforcement** | actually block USB/print/sites/processes (reflect-only today) | 🔴 config-profile / system-extension class | **MDM-class** | 🔴 **RISK — see §6; DO NOT BUILD** | TT-5/TT-6 deferred policy |
| 3 | **Chat SEND** | text input → `ChatBroadcast`/`ChatDirect` (receive exists) | no | none | S · LOW | TT-7 |
| 4 | **Reaction SEND** | emoji picker → `Reaction` | no | none | S · LOW | TT-7 |
| — | *(Hand-raise SEND)* | *already exists (`RaiseHand` command)* | — | — | **done** | TT-7 |
| 5 | **Teacher-screen RECEIVE + display** | dispatch `ScreenStreamFrame`/`Stop` + wire the (built) handle-based decoder + fullscreen viewer | reuse decoder | **none** (decode needs no Screen Recording) | M · MED | TT-8 |
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
- **Against full customer-B scope: ~55%.** Remaining = 11 items, incl. two **L·HIGH** (breakout,
  quiz), one **HIGH** (policy enforcement — possibly unreachable without MDM), and three new-native
  (remote inject, AVPlayer, capture-to-file is teacher-side). The base half is the bigger *effort*
  chunk; the remaining half is the broader *feature* surface.
- **The "95%" was measuring the wrong denominator.** Recorded so it isn't quoted again.

---

## 6. 🔴 RISK (not a task) — policy enforcement is MDM-class · pending sales (2026-07-14)

Item #2 is a **RISK, not a scheduled task.** Real enforcement of USB / printing / app / site blocking
on macOS is **not achievable by an ordinary app** (sandboxed or not) — it requires MDM enrollment or
installed configuration profiles. This is a **business blocker**, not an engineering task.

**Actual macOS mechanisms — and whether they work WITHOUT MDM:**

| Restriction | macOS mechanism | Without MDM? |
|---|---|---|
| USB / external media | config-profile media-access restriction / MDM `Restrictions` payload; USB Restricted Mode | ❌ no app API; needs profile + supervision |
| Printing | MDM / config-profile print restriction | ❌ no system-wide app-level block |
| App launching | Screen Time / `com.apple.applicationaccess` payload | ❌ MDM/config-profile; legacy app API removed |
| Web / site filtering | content-filter Network Extension (special entitlement + user approval) or MDM web-content-filter | ⚠️ NE possible but heavy (entitlement + approval) |

Config profiles not delivered by MDM must be **manually installed**, and many payloads need
**supervision** (Apple Business Manager). **Bottom line: without MDM, macOS policy enforcement is
effectively unavailable to this app.**

**Business questions (for sales):**
1. Are customer B's 50 Macs enrolled in an MDM (Jamf / Mosyle / ABM-supervised)?
2. If not — is "policy does not enforce on Mac" acceptable, or a dealbreaker?
3. If they'd need to buy/deploy MDM — that's cost + IT work **outside our scope**.

The honest customer answer may be **"policy is Windows-only."** Say it early, not at delivery.
🔴 **DO NOT attempt policy enforcement until sales returns.**
