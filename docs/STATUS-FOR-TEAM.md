# macOS ClassroomCtrl — Status for the Team (manager + sales) · 2026-07-14

One page, plain language. Detail in `PROJECT-HANDOVER.md` / `TOR-COMPLIANCE.md`.

## 🔴 The headline for sales (proven LIVE 2026-07-14)
**A Mac teacher can play a video, and your existing UNMODIFIED Windows student machines see the
picture AND hear the sound — with zero changes to the shipped Windows software.** Confirmed live
against a real shipped Windows v1.2 student. It's the clearest proof that the customer's current
Windows PCs keep working alongside a Mac teacher.

## What works today
A macOS **Student** app that a real Windows-based classroom can already use, and a macOS **Teacher**
app that does the core of a lesson: see every student's screen, lock/unlock, send commands, chat +
hand-raise + reactions, **share the teacher's screen**, **hear students** (mixed), and **talk to the
class**. It talks to the existing Windows software **byte-for-byte** — a Windows student and a Mac work
together unchanged. **Honest completion vs the full customer-B contract: ~55%.** Teacher ~55% (teacher audio both directions + share-screen-with-sound, all LIVE), Student
shippable for the base classroom.

## What doesn't work yet, and why
The **hard half is still ahead** and it **needs a Mac to build and test on**: the live conference
(camera + everyone-hears-everyone), file/movie distribution, remote control, recording, breakout
rooms, and the quiz system. None of it can proceed without Mac hardware.

**Two contract items are impossible on macOS by Apple's design — not our software:**
1. **Remote power-ON** of a student Mac (TOR 11.2.1): Apple Silicon Macs can't be switched on over the
   network from "off." Workarounds exist (sleep-instead-of-shutdown + wake, or a morning auto-on
   schedule) — needs a customer decision.
2. **Policy enforcement** (blocking USB/printing/apps, TOR 11.2.14): impossible on macOS without MDM
   enrollment, which the customer doesn't have. This is **Windows-only.**

**Two deployment risks to raise with the customer now:**
- **The iMac M4 may have no Ethernet port** (base model's Ethernet is an at-purchase-only option in the
  power adapter). On a wired-LAN school this is a **blocker — verify the exact model they bought.**
- **In-room conference audio would howl** — 50 Macs with open mics + speakers in one room feed back
  acoustically (no software can fix this in a shared room). Is the conference for remote students, or
  will students wear headsets?

## 🎁 Immediate value, no Mac needed — 7 Windows bugs
Building the Mac port meant reading the Windows code closely, which uncovered **7 real bugs in the
shipped Windows product** — including one that **removes the wrong student from the list** when someone
disconnects in a class of more than one, and a family of four where teacher commands (lock, power,
stop-sharing, hand-lower, audio-stop) can be **silently dropped under load**. **All are documented with
exact file+line and fixes; the Windows team can act today** (`PROJECT-HANDOVER.md` §3). This is real,
shippable value that benefits **both** customers regardless of the Mac situation.

## The blocker, blunt
**No Mac = no macOS delivery.** The borrowed MacBook is gone. To finish customer B's all-Mac classroom
we need Mac hardware:
- **Minimum: 1 Apple-Silicon Mac** (a **Mac mini M-series is ~$599**).
- **Better: 2** — the flagship conference feature **cannot be tested on one machine** (and the
  wrong-student bug above hid for three phases *because* we only had one).
Until then, macOS work is stopped. The Windows bug-fixes are the value we can ship in the meantime.
