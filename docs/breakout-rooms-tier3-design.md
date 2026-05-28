# Breakout Rooms — Tier 3 Design (Sketch + Risk-Marked)

**Status:** sketch, with explicit risk callouts. NOT implementation-ready.
**Prereq read:** [`breakout-rooms-architecture.md`](./breakout-rooms-architecture.md),
[`breakout-rooms-tier1-design.md`](./breakout-rooms-tier1-design.md),
[`breakout-rooms-tier2-design.md`](./breakout-rooms-tier2-design.md).
**Estimated effort:** 5–7 days nominal; **+1–3 days slip risk** depending on AEC outcome.
**Triggers Tier 3 readiness:** Tier 1 + Tier 2 shipped + validated; customer confirms voice
is wanted; mic hardware survey done (addendum: design for worst case = built-in laptop mic+speaker).

---

## 1. Scope

Tier 3 delivers **per-group voice chat** with addendum overrides applied:
- **PTT default** (Decision 5 override): push-to-talk, hold-key-to-speak, configurable hotkey
  (Space by default).
- **Always-on mode** is explicit per-student opt-in.
- **Star topology** (Decision 1): teacher relays voice — no P2P.
- **Student-side mixing** (Decision 4 — SFU-style): each student mixes the N-1 inbound
  streams from group peers locally; teacher CPU bounded.
- **Layered AEC strategy** (addendum):
  1. PTT serialization (biggest single mitigation).
  2. WASAPI AEC mode on capture endpoint.
  3. Document "USB headset recommended" for residual cases.
- **Mic device handling**: default capture device; hot-plug via NAudio MMNotificationClient;
  graceful "no mic detected" state (student still receives others' audio).
- **Privacy indicator** (Decision 5 corollary): persistent banner when mic is live.

**Out of scope:** Opus encoding (deferred); voice-activity classroom analytics; recording
group voice; teacher-side voice mixing.

---

## 2. UX

### 2.1 Student-side mic UI

A small persistent **MicWidget** docked at the bottom-right of the agent main window (next to
existing tray-related UI). Three states:

```
┌─ 🎤 Off ──┐    ┌─ 🎤 PTT (Space) ──┐    ┌─ 🔴 ON · Always ──┐
│  [Enable] │    │  Hold Space       │    │ 🟢 Speaking       │
└───────────┘    └───────────────────┘    │  [Mute]           │
                                          └───────────────────┘
```

- **Off (default).** Mic not capturing. Click `Enable` → choose PTT or Always-On (modal).
- **PTT.** Captures only while hotkey is held. Hotkey configurable; default `Space`.
  Visual cue: indicator flashes red while held + a small voice-activity dot lights when RMS
  > threshold.
- **Always-On.** Captures continuously. Indicator red and persistent. `[Mute]` toggles to Off
  without losing the always-on preference.

### 2.2 Mic privacy banner

A separate `MicLiveBanner` window — pinned top-center of screen — visible only when mic is
actively capturing. Mirrors `RemoteControlBanner` style:

```
┌─────────── 🎤 Microphone ON · Group 1 ───────────┐
└──────────────────────────────────────────────────┘
```

Always-On: persistent. PTT: visible only while hotkey is held. Cannot be dismissed by the
student (closes when mic turns off).

### 2.3 Teacher controls

- `GroupExpandedView` per-cell: voice-activity meter + mute button (force-mute via
  `MicMuteRequest`).
- `GroupManagerView` per-group: aggregate "active speakers" indicator.
- Toolbar: "Mic Override" — set every student in a group to forced-mute (e.g. when teacher
  wants to address everyone).

### 2.4 No-mic case

If the student has no capture device (or the default device disappears mid-session), the
MicWidget shows "🎤 No mic detected" + tooltip "Voice chat receive-only". Student still
receives others' voice; they just can't speak.

---

## 3. Major components (new code)

### 3.1 `StudentMicBroadcaster` *(NEW, `src/ClassroomCtrl.Student.Agent/`)*

Mirror of teacher's `AudioBroadcaster.cs` shape but:
- Source: `NAudio.Wave.WaveInEvent` on default capture device (mic), not WASAPI loopback.
- Capture format: native → resample to 16 kHz mono 16-bit (same shape as teacher loopback path).
- **Frame production: drive off the actual data-available callback** (lesson from Phase 11-C
  v2 Step 1) — NOT `Task.Delay(100ms)`. Capture buffer cap small (300 ms).
- Emit `VoiceAudioFrameMessage` (architecture.md §4) with `SourceEndpointId = me`,
  `GroupId = _myRoomId`, monotonic `FrameSeq` per session.
- Routes via IPC → Service → TCP teacher → relay-fanout (§3.3).
- PTT gate: a single bool `_pttActive` set by the low-level keyboard hook; capture continues
  but frames are dropped while `!_pttActive` (cheaper than start/stop the WaveIn each press).
- Always-on: gated by `_alwaysOnEnabled` + `_muted`.

### 3.2 `GroupVoicePlayer` *(NEW, `src/ClassroomCtrl.Student.Agent/`)*

Mirror of `AudioPlayer.cs` extended for multi-source. Internal layout:

```
              VoiceAudioFrame(SourceA)        VoiceAudioFrame(SourceB)
                       │                                │
                       ▼                                ▼
              BufferedWaveProvider(A,             BufferedWaveProvider(B,
                  cap=300ms)                          cap=300ms)
                       │                                │
                       └─── MixingSampleProvider ──────┘
                                       │
                                       ▼
                            WaveOutEvent (mix output, 100ms latency)
```

- One `BufferedWaveProvider` per active source endpoint; bounded jitter buffer per source
  (200 ms pre-buffer, 400 ms soft cap, same as 11-C `AudioPlayer`).
- New sources auto-created on first `VoiceAudioFrame` arrival from a previously-unseen
  endpoint; auto-removed when a source goes idle > ~3 s (avoid leaks if a peer leaves
  without explicit signal).
- Single `WaveOutEvent` plays the `MixingSampleProvider` mix.
- Skip-ahead policy per source: identical to 11-C `AudioPlayer.SoftCapMs`.

### 3.3 `ControlServer` voice relay (extension)

- On receipt of `VoiceAudioFrame` from sender S in group G:
  - Validate: `_studentRoomMap[S] == G` (defensive).
  - Fan out to all members `M ∈ G` where `M != S`.
  - Route via new `_voiceOutbox` (§3.5).
- On `MicStateUpdate`: store latest state per student; expose to teacher UI for indicators.
- On teacher `MicMuteRequest`: targeted send to specific student.
- On teacher `MicPttSet`: broadcast or per-student set of PTT/AlwaysOn mode + hotkey.

### 3.4 `TcpControlServer._voiceOutbox` *(NEW)*

New per-peer channel:
- Channel type: `Channel<byte[]>`.
- `BoundedChannelFullMode.DropOldest`, cap 6 (~600 ms × 100 ms frames, multi-source aware —
  3 active sources × 2 frames each before drops kick in).
- Reason for separation from `_audioOutbox`: teacher's loopback audio rides `_audioOutbox`
  (cap 3); voice can be 4× higher rate at peak, so sharing would push teacher audio out
  of the queue.
- Writer-loop priority update: `reliable → audio → voice → input → one lossy`. Audio still
  comes first (teacher's broadcast-to-class loopback is the higher-priority "everyone hears
  me" path).
- New `EnqueueVoiceOrDrop(body)` method on `PeerConnection`.
- New `BroadcastVoiceToGroupAsync(env, groupId, excludeSourceId)` on `TcpControlServer`:
  iterate `_peers.Values`, fan out via `EnqueueVoiceOrDrop` — receivers filter by `IsForMe`
  (which already includes `TargetGroupId` from Tier 1).

### 3.5 Low-level PTT hotkey hook *(NEW, `src/ClassroomCtrl.Student.Agent/`)*

`PttHotkeyHook` — `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` style hook running in the Agent.
- On key-down for the configured VK (default `VK_SPACE`): set `_pttActive = true`.
- On key-up: `_pttActive = false`.
- **CRITICAL:** pass-through — do NOT consume the key, so normal typing still works.
  Specifically: in the hook callback, return `CallNextHookEx(...)` unconditionally; only the
  side-effect of setting `_pttActive` is ours.
- Hot-reconfigurable hotkey: store as string ("Space", "F12", etc.) → resolve to VK at hook
  install.
- Disposed on Agent shutdown; `_pttActive = false` on dispose.

### 3.6 `MicLiveBanner` *(NEW window)*

WPF window, sized small, pinned center-top with `WS_EX_TOOLWINDOW` + topmost. Same style
language as `RemoteControlBanner` for visual consistency.

---

## 4. Wire messages

From architecture.md §4 (Tier 3 codepoints):

| Code | Type | Direction | Channel |
|---|---|---|---|
| `0x0640` | `VoiceAudioFrame` | S → T → group peers | new `_voiceOutbox` |
| `0x0641` | `MicMuteRequest` | T → S | `_reliableOutbox` |
| `0x0642` | `MicStateUpdate` | S → T | `_reliableOutbox` |
| `0x0643` | `MicPttSet` | T → S | `_reliableOutbox` |

Wire DTOs in architecture.md §4 Tier 3 DTOs.

---

## 5. AEC strategy (3 layers)

### Layer 1: PTT default (DONE — Decision 5 override)

Serializes speakers; greatest single mitigation. Mathematics: even with mic-and-speaker
co-located, the speaker is silent when PTT is up → no feedback path.

### Layer 2: WASAPI AEC on capture endpoint *(SPIKE REQUIRED)*

Windows native AEC, available since Vista, exposed via:
- `IAudioClient` with `AUDCLNT_STREAMFLAGS_RAW` cleared (enables system effects pipeline).
- Or via `IAudioCaptureClient` + `MMDeviceEnumerator` to find an endpoint with the AEC effect.
- NAudio wrapper: `WasapiCapture` with explicit AudioClientStreamFlags = None (default
  includes the effects pipeline on consumer devices).

**Spike before implementation primer**: on real hardware (built-in laptop mic + speaker),
verify:
1. Does `WasapiCapture` (default flags) actually engage AEC? Test by playing teacher loopback
   into speakers + capturing mic and looking for echo cancellation.
2. Are there NAudio knobs to force the effects chain? `AudioClientShareMode.Shared` + default
   stream flags should suffice on Windows 10+.
3. Behavior when no AEC effect is installed (some IT-managed Windows builds disable system
   effects). Fallback: software AEC library (WebRTC.NativeApm) — pulls in C++ dependency,
   probably too heavy.

**If AEC isn't reliably available**: rely on PTT (Layer 1) and document USB headset (Layer 3).
This is acceptable per addendum.

### Layer 3: Document "USB headset recommended"

Customer-setup notes; not a code change. Flag for the customer's IT support.

---

## 6. PTT design details

- **Default hotkey:** `Space-bar` (`VK_SPACE`).
- **Configurable** per student in the MicWidget settings.
- **Capture mechanism:** `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` low-level hook in the
  Agent. **NOT WPF KeyDown** (only fires when Agent has focus, which it doesn't most of the
  time).
- **Pass-through:** hook returns `CallNextHookEx` always — Space still types a space in
  whatever app is focused.
- **Side-effect**: hook handler atomically sets `_pttActive`. Mic-broadcaster picks it up on
  the next frame boundary (~100 ms granularity is fine for voice).
- **Repeated key-down**: Windows fires key-down repeatedly while held. Hook coalesces — only
  the transition matters (`_pttActive = true` is idempotent).
- **Modifier hotkeys** (e.g., Ctrl-Space): out of scope for v3; basic VK match.
- **Hotkey conflict** with other apps that hook Space (very rare): document. If Space is the
  conflict, the user reconfigures.

---

## 7. Bandwidth math (validating addendum)

Per addendum (40 students, 8 groups of 5 = 4 active + 1 host per group + 1 teacher monitoring):

- **PCM 16 kHz mono 16-bit** = 32 KB/s per voice stream = **256 kbps**.
- **Per-student incoming (steady state):** 4 peers × 256 kbps = **1.0 Mbps**.
- **Teacher relay aggregate (worst case = all 40 talking simultaneously, PTT serialization
  ignored — never happens but bounds the math):**
  - 40 inbound = 40 × 256 kbps = **10 Mbps** at teacher's NIC.
  - Each frame fanned to up to 4 group peers = 40 × 256 × 4 = **40 Mbps egress**.
- **PTT realistic (1 speaker per group at a time):** 8 inbound × 256 kbps = **2 Mbps** in;
  8 × 4 = 8 Mbps out. **Comfortably small.**
- **With Tier 1 + Tier 2 concurrent screen-share** (teacher whole-class share 500 kbps × 40 =
  20 Mbps; one presenter per group 8 × 500 kbps × 4 = 16 Mbps fan-out = 36 Mbps total
  screen): + 8 Mbps PTT voice = **44 Mbps total**. Within gigabit; tight on 100 Mbps.

**Opus codec (deferred)** would cut voice ~10× (256 → 24-32 kbps). Worth pursuing only if
class scales beyond 60–80 students or LAN drops to Wi-Fi.

---

## 8. Risks (explicit, ranked)

### R1 — AEC outcome unknown until spike

**Owner risk:** Tier 3 effort estimate (5–7 days) assumes Layer-2 WASAPI AEC works
out-of-the-box. If the hardware spike (recommended on day 1 of Tier 3) finds it doesn't,
fallback paths are PTT-only (still acceptable per Layer 1 mitigation) or USB-headset
documentation. The Tier 3 design does NOT depend on AEC for correctness, only for quality of
the built-in mic+speaker case.

**Mitigation:** Day-1 hardware spike. If AEC fails, communicate to customer as "PTT-only on
built-in mic; USB headset recommended" — design unchanged.

### R2 — Low-spec student CPU mixing 4-5 streams

NAudio mixing + WASAPI output on a 7th-gen i5 is ~1-2% CPU. Atom / Celeron / very old fleets
could see 5-10% which is still fine. **Mitigation:** flagged for customer hardware identification
before implementation. If problem arises: cap mixing to 3 simultaneous sources (mute oldest);
worst case fall back to teacher-side mixing per-recipient (Decision 4 alternative).

### R3 — Privacy indicator visibility

Banner approach works visually but loses to fullscreen-exclusive apps (games, presentations
in slideshow mode). **Mitigation:** also blink the system tray icon while mic is live; add a
notification balloon on first mic-on per session. Don't try to overlay fullscreen apps
(brittle + intrusive).

### R4 — PTT hotkey conflict with focused app

If user is typing in a heavy app that also hooks Space (rare), behavior could be unpredictable.
**Mitigation:** hotkey is configurable; default Space is the well-known voice-chat convention
(Discord, Mumble) so users will already expect it.

### R5 — Teacher restart loses mic state

Mic states are recoverable: students broadcast `MicStateUpdate` periodically (every 5 s or on
change). Teacher recovers full state within 5 s of restart.

### R6 — Echo loop if both PTT and Always-On students co-exist in same group with built-in
hardware

Possible: Student A on Always-On, Student B on PTT. While B holds PTT, B's mic captures A's
voice from B's speakers + own voice → relayed back to A → A's speakers play own voice
delayed. Self-feedback at A. **Mitigation:** Layer-2 AEC (if available); otherwise document
"prefer PTT for everyone in a group" as a teacher-policy hint.

### R7 — Voice-activity false positives in noisy classrooms

RMS threshold tuning. **Mitigation:** simple energy-history filter (require N consecutive
above-threshold frames before lighting indicator) + per-student auto-calibration on session
start (sample 1 s of ambient and set threshold = ambient + 12 dB). Polish, not blocker.

### R8 — Hot-plug USB headset mid-session

NAudio's `MMNotificationClient` fires on device change. **Plan:** subscribe in
`StudentMicBroadcaster`; on default-device change, stop+restart capture transparently.
**Risk:** brief audio dropout. Acceptable.

---

## 9. Effort estimate

| Sub-task | Days | Risk |
|---|---|---|
| Wire protocol additions (codepoints + DTOs + voice channel) | 0.5 | low |
| AEC hardware spike (Layer 2 validate) | 0.5 | medium |
| `_voiceOutbox` per-peer channel + writer-loop priority | 0.5 | low |
| `StudentMicBroadcaster` + capture device handling | 1.0 | medium |
| PTT hotkey hook + pass-through | 0.5 | low |
| `GroupVoicePlayer` multi-source mixing | 1.0 | medium |
| Teacher-side voice relay + state map | 0.5 | low |
| MicWidget + MicLiveBanner UI | 0.5 | low |
| Teacher controls (force-mute, override) | 0.25 | low |
| Multi-PC validation (3-4 PCs required) | 1.0 | medium |
| Polish + bug fixes | 0.75 | medium |
| **Nominal total** | **7.0** | |
| **Slip risk** | **+1–3 days** | AEC outcome / mixing CPU on slow boxes |

**Total realistic envelope: 7–10 days.** If estimate is tight, the customer-facing-quality
nice-to-haves (VAD tuning, hot-plug grace, etc.) can slip to a v3.x follow-up. The voice path
itself + PTT is the core; AEC is the quality lever.

---

## 10. Multi-PC validation requirement

**2-PC won't validate voice chat.** Group voice fundamentally needs ≥ 3 endpoints:
- PC1 — teacher (relay).
- PC2 — student A (group 1).
- PC3 — student B (group 1).
- ideally PC4 — student C (group 1) so we can exercise 3-stream mixing on B and C.

If 4 PCs aren't available, the implementation primer should call out "Tier 3 acceptance
requires multi-PC; until then, dev validates the structural pieces (capture, transport,
mixing) but acceptance is provisional".

---

## 11. After this round

If addendum unknowns (mic hardware reality, AEC viability on customer fleet) shift the
design materially, this doc updates BEFORE the Phase 13-D implementation primer drops. The
implementation primer is scoped to Tier 3 only; Tier 4 (Opus, advanced AEC, recording per
group) is post-v1.

Voice + Tier-1/Tier-2 together close out Feature #3. v1 ships with Features #1 + #2 + #3.
