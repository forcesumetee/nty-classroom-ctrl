# TOR Compliance — macOS (iMac M4) · as of 2026-07-14

Status of the TOR items we have visibility on, for the customer conversation. **Two items cannot be
met on macOS as written — both are Apple platform constraints, not product defects.** This doc should
be reconciled against the FULL TOR (no TOR document exists in either repo — these items surfaced
through implementation; verify numbering with the contract).

Legend: ✅ met · 🟡 met with a caveat · 🔴 cannot be met as written (platform constraint) · ⏳ buildable, not yet built.

| TOR | Item | macOS status | Notes |
|---|---|---|---|
| **11.2.1** | สั่ง **เปิด**-ปิดเครื่องลูกข่าย (power **ON**/off) | 🔴 **power-ON cannot be met as written** | See below. Power-OFF/restart/logoff: ⏳ (Mac student power-execution unbuilt — needs NSWorkspace/`osascript` + Automation TCC). Windows students: ✅. |
| **11.2.14** | Policy enforcement (USB / printing / app / site blocking) | 🔴 **Windows-only — impossible on macOS without MDM** | No app-level API; needs MDM enrollment / config profiles. Customer B has no MDM. Tell them before deployment. |
| (audio) | Teacher hears students / talks to class | ✅ **built + LIVE** (TT-9 mixer + TT-10-B Talk) | teacher mixes N students (cap 12, gain-norm); teacher mic → all students. |
| **11.2.9** | Recording/sharing teacher screen **+ audio** (incl. system audio) | ✅ **RESOLVED — LIVE-confirmed 2026-07-14** | SCK `capturesAudio` + screen coexist in one SCStream — **proven LIVE on real hardware, cross-platform to an UNMODIFIED shipped Windows student** (video: picture + sound). No third-party driver, no wire change. TT-10-B (teacher mic) + TT-10-C (system audio) both LIVE; normal quality (16k-mono honored). TCC = Screen Recording (TT-8 grant). **Open customer question:** "เสียงของครู" = mic-only or system audio? — **we can now offer BOTH.** See `TT-10-BC-LIVE-CONFIRMATION.md`. |
| (conf) | Conference / peer audio (everyone hears everyone) | 🔴/⚠️ **usability blocker in a shared room** | Achievable technically (rides `VoiceAudioFrame` 0x0640, no wire change), BUT **acoustic coupling** (Finding 4) makes it howl with 50 in-room iMacs unless remote students / headsets. Ask sales. |
| (screen/control) | See student screens, lock, commands, chat, share screen, remote control | ✅ built (TT-0…TT-8) / ⏳ remote-control (TT-14 unbuilt) | roster, MJPEG+H.264 view, lock/unlock, power (Win), multi-select+bulk, chat/hand/reactions, Share My Screen. |

## 🔴 11.2.1 power-ON — the detail (status changed from "❓ must check" to "🔴 cannot be met as written")
- **Wake-on-LAN wakes a Mac from SLEEP only, never from full shutdown.** On **Apple Silicon** a
  powered-off Mac's NIC loses power → cannot receive a magic packet. **No network cold-boot exists on
  Apple Silicon.** Apple platform constraint.
- **Shipped Windows never implemented power-ON/WoL either** (verified read-only: no magic-packet
  sender, no PowerOn message type, no MAC capture, no requirement doc). So this is **net-new**, not a
  regression from Windows parity.
- **Workarounds that deliver the intent (machines ready at class start) — propose BOTH:**
  - **(a) Sleep, not shutdown** + WoL (magic packet; "Wake for network access" on): teacher CAN wake
    sleeping Macs. Needs an Energy-Saver policy on student machines. **Buildable, S–M** (UDP
    magic-packet broadcaster on the Teacher + student MAC via ARP from the socket IP — no wire change).
  - **(b) Scheduled power-on** (`pmset repeat poweron MTWRF 07:30:00`): not network-triggered, but on
    before class. Zero code.

## 🔴 Deployment prerequisite (Finding 2) — verify the iMac SKU
24" iMac Ethernet lives in the **power adapter**. **2-port base iMac M4 = no Ethernet, cannot be added
after purchase.** The school is on wired LAN → if they bought base units, 50 machines have no wired
NIC. **Verify the exact SKU before shipping.** WiFi fallback re-introduces client-isolation + makes the
50-mic bandwidth (12.8 Mbps) a real concern.

## Note (Finding 3): all CPU numbers are worst-case
Measured on the borrowed **M2**; the target **M4** is faster with a better media engine. No CPU concern.
