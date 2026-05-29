# Conference Mode — UI Mockups

**Status:** investigation. Layouts + toolbar wire-frames + responsive
breakpoints for Phase 15-C/D implementation primers.
**Parent:** [`conference-mode-ux-architecture.md`](./conference-mode-ux-architecture.md).
**Last updated:** 2026-05-29.

All mockups assume the **teacher's** main window in Conference mode
(`CurrentMainView = Conference`). Student.Agent's `ConferenceGalleryWindow`
is visually identical except: no admin menu in the more-button (`⋮`),
self-tile labeled "(You)", End button replaced with "Leave".

Window minimum: 1280×720. Recommended: 1366×768+.

---

## 1. Window-level layout (Conference active)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Logo]  Classroom Ctrl   📚 Classroom  [📹 Conference (●)]   👤 Connected: 4 │  ← Header 56px
│                                                            🔔 🌐 ⚙           │   (toggle = pill)
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│                                                                              │
│                                                                              │
│                                                                              │
│                       ◆ GALLERY VIEWPORT ◆                                   │  ← Content area
│                       (auto-layout per N)                                    │   takes Row 2
│                                                                              │   Col 1+2
│                                                                              │   (chat rail and
│                                                                              │    sidebar both
│                                                                              │    collapsed)
│                                                                              │
│                                                                              │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│  🎙 Mic   📷 Cam   🖥 Share   💬 Chat   ✋ Hand   ⋮ More            📞 End    │  ← Toolbar 64px
└──────────────────────────────────────────────────────────────────────────────┘

Header notes:
- Mode toggle is a 2-state pill in the header right-cluster (next to bell + cog).
- Active mode capsule is highlighted; inactive is greyed.
- Click toggles the *view*; teacher must still click Start Conference (large
  button on the empty gallery viewport) to actually emit ConferenceStart.

Sidebar (240 left col) and chat rail (320 right col) BOTH collapse when
Conference is active.  The gallery uses Grid.ColumnSpan="3" so the full
viewport width is available.
```

---

## 2. Empty / waiting state (just teacher, no other participants)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Logo]  Classroom Ctrl   📚 Classroom  [📹 Conference (●)]   🔔 🌐 ⚙        │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│                                                                              │
│                              📹  Conference                                  │
│                                                                              │
│                         No participants yet.                                 │
│                                                                              │
│                                                                              │
│                    ┌───────────────────────────────┐                         │
│                    │     [▶ Start Conference]      │                         │
│                    └───────────────────────────────┘                         │
│                                                                              │
│                                                                              │
│                  Invites all connected students to join.                     │
│                                                                              │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│  🎙 Mic   📷 Cam   🖥 Share   💬 Chat   ✋ Hand   ⋮ More            📞 End    │
└──────────────────────────────────────────────────────────────────────────────┘

Pre-start state:
- Toolbar buttons appear but Mic / Cam / Share are inert (or pre-flight only).
- Teacher can test cam (banner shows; cam doesn't broadcast yet).
- "Start Conference" is the primary CTA.
- Click → confirm dialog if breakouts exist (per architecture § 5 risk #2) →
  emit ConferenceStart → state moves to § 3.
```

---

## 3. 1 participant solo (teacher only, post-start)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Logo]  Classroom Ctrl   📚 Classroom  [📹 Conference ●]   🔔 🌐 ⚙          │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│                                                                              │
│                                                                              │
│                          ┌─────────────────────┐                             │
│                          │                     │                             │
│                          │     [TEACHER CAM]   │                             │
│                          │                     │                             │
│                          │                     │                             │
│                          │   Teacher (You)     │                             │
│                          │   🎙 [unmuted]      │                             │
│                          └─────────────────────┘                             │
│                                                                              │
│                                                                              │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│  🎙●  📷●  🖥 Share   💬 Chat   ✋ Hand   ⋮ More              📞 End          │
└──────────────────────────────────────────────────────────────────────────────┘

- Single tile centered, ~50% of viewport.
- Self-label "(You)".
- Mic indicator overlay bottom-left.
- Active mic/cam toggles show dot indicator (●).
```

---

## 4. 2–4 participants (2×2 grid)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Header                                                                       │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌───────────────────┐  ┌───────────────────┐                               │
│   │                   │  │                   │                               │
│   │     [TEACHER]     │  │   [STUDENT A]     │                               │
│   │                   │  │                   │                               │
│   │   Teacher (You)   │  │  Som C. ✋        │   ← hand raised badge        │
│   │   🎙              │  │  🎙 📷            │                               │
│   └───────────────────┘  └───────────────────┘                               │
│                                                                              │
│   ┌───────────────────┐  ┌───────────────────┐                               │
│   │                   │  │  ╔═══════════════╗ │   ← active speaker accent   │
│   │     [STUDENT B]   │  │  ║  [STUDENT C]  ║ │     (2px green border)      │
│   │                   │  │  ║               ║ │                              │
│   │   Pim P.          │  │  ║  Ake R. 🟢   ║ │                              │
│   │   🚫 (cam off)    │  │  ║  🎙           ║ │                              │
│   └───────────────────┘  └──╚═══════════════╝─┘                              │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│  Toolbar                                                                     │
└──────────────────────────────────────────────────────────────────────────────┘

Tile size: ~480×270 each (16:9 at half-width).
Gap: 16px between tiles.
Margins: 24px from viewport edge.
```

---

## 5. 5–9 participants (3×3 grid)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│   ┌──────────┐  ┌──────────┐  ┌──────────┐                                   │
│   │   [T]    │  │   [S1]   │  │  [S2 ✋] │   ← hand-raised badge top-right │
│   │ Teacher  │  │ Som C.   │  │ Pim P.   │                                   │
│   │   🎙     │  │ 🎙 📷    │  │ 🎙       │                                   │
│   └──────────┘  └──────────┘  └──────────┘                                   │
│                                                                              │
│   ┌──────────┐  ┌══════════┐  ┌──────────┐                                   │
│   │   [S3]   │  │ ║ [S4]   ║│  │   [S5]   │   ← S4 active speaker            │
│   │ Tao K.   │  │ ║ Ake R. ║│  │ Note B.  │                                   │
│   │ 🚫       │  │ ║ 🟢🎙   ║│  │ 🎙       │                                   │
│   └──────────┘  └══════════┘  └──────────┘                                   │
│                                                                              │
│   ┌──────────┐  ┌──────────┐  ┌──────────┐                                   │
│   │   [S6]   │  │   [S7]   │  │   [S8]   │                                   │
│   │ Tar L.   │  │ Mint S.  │  │ Don P.   │                                   │
│   │ 🎙       │  │ 🎙       │  │ 🎙       │                                   │
│   └──────────┘  └──────────┘  └──────────┘                                   │
└──────────────────────────────────────────────────────────────────────────────┘

Tile size: ~340×190 each (16:9).  Reactions float briefly on tile.
```

---

## 6. 10–16 participants (4×4 grid)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│   ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                │
│   │ T  │ │ S1 │ │ S2 │ │ S3 │     Tile ≈ 240×135                            │
│   │🎙  │ │ ✋ │ │    │ │ 🚫 │     Names truncated to 8 chars                │
│   └────┘ └────┘ └────┘ └────┘                                                │
│   ┌────┐ ┌════┐ ┌────┐ ┌────┐                                                │
│   │ S4 │ │║S5║│ │ S6 │ │ S7 │     S5 active                                 │
│   └────┘ └════┘ └────┘ └────┘                                                │
│   ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                │
│   │ S8 │ │ S9 │ │S10 │ │S11 │                                                │
│   └────┘ └────┘ └────┘ └────┘                                                │
│   ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                │
│   │S12 │ │S13 │ │S14 │ │S15 │                                                │
│   └────┘ └────┘ └────┘ └────┘                                                │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 7. 17+ paginated (next-page button)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                          16 tiles (Page 1 of 2)                              │
│                                                                              │
│   ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                │
│   │ T  │ │ S1 │ │ S2 │ │ S3 │                                                │
│   └────┘ └────┘ └────┘ └────┘                                                │
│   …    (4×4 grid as page 6)                                                  │
│                                                                              │
│                         ◀ [Page 1 / 2] ▶                                     │
└──────────────────────────────────────────────────────────────────────────────┘

Page button at bottom of viewport, above toolbar.
N=17 → page 1 shows 16, page 2 shows 1 (centered).
N=32 → page 1 shows 16, page 2 shows 16.
Active speaker auto-pages to whoever is speaking (configurable; default ON).
```

---

## 8. Pinned / spotlighted layout (1 large + filmstrip)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│   ┌──────────────────────────────────────────┐  ┌────┐                       │
│   │                                          │  │ T  │                       │
│   │                                          │  └────┘                       │
│   │           [PINNED PARTICIPANT]           │  ┌────┐                       │
│   │                                          │  │ S1 │                       │
│   │            Som C.  🟢 (speaking)         │  └────┘                       │
│   │                                          │  ┌────┐                       │
│   │                                          │  │ S2 │                       │
│   │                                          │  └────┘                       │
│   │                                          │  ┌────┐                       │
│   │                                          │  │ S3 │                       │
│   └──────────────────────────────────────────┘  └────┘                       │
│                                                  + [more ▼] (scroll strip)   │
│                                                                              │
│   Click a tile to pin / unpin.  Right-click → "Spotlight for all"            │
│   (broadcasts WebcamPresenterSet 0x0656 from Phase 14-A Tier 3 — wire        │
│    proposed; codepoint not added until 15-F).                                │
└──────────────────────────────────────────────────────────────────────────────┘

Large tile: ~75% width.  Filmstrip: 20% width, scrollable column.
```

---

## 9. Bottom toolbar wire-frame (detail)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  ┌──┐ ┌──┐ ┌────────┐ ┌──────┐ ┌──────┐ ┌──┐               ┌──────────┐    │
│  │🎙│ │📷│ │🖥 Share │ │💬 Chat│ │✋ Hand│ │⋮ │               │📞 End     │    │
│  └──┘ └──┘ └────────┘ └──────┘ └──────┘ └──┘               └──────────┘    │
│  toggle toggle toggle  toggle    toggle  menu              danger (red)    │
└──────────────────────────────────────────────────────────────────────────────┘

  Button   State (off / on)                          Wire path
  ──────   ────────────────────────────              ──────────────────────
  🎙       grey / accent + dot                       Phase 13-D MicBroadcaster
                                                     (existing toggle)
  📷       grey / accent + red banner appears        Phase 14-B ToggleCamera
                                                     (existing command)
  🖥 Share  grey / accent                             Phase 11-B ShareScreenCmd
                                                     (existing)
  💬 Chat   inactive / count badge if unread          Opens slide-in sidebar
                                                     (reuses ChatRoom)
  ✋ Hand   inactive / yellow + on tile               NEW 0x0660 HandRaise
                                                     / 0x0661 HandLower
  ⋮ More   opens popup with Participants,            NEW (composite)
           Settings, Reactions (👍❤️😂😮😢), Record
           (disabled w/ tooltip)
  📞 End    teacher only — broadcasts                 NEW 0x0671 ConferenceEnd
           ConferenceEnd to all                       Student-side: shows
                                                     "Leave" instead, sends
                                                     opt-out to teacher

Toolbar background: dark glass (#1F2937 with 0.85 opacity), rounded top.
Auto-hide after 3 s of mouse inactivity (configurable, default OFF for
desktop; Meet auto-hides).
```

---

## 10. Chat sidebar (slide-in panel)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Toolbar 💬 clicked, sidebar slides in from right, overlay over gallery]    │
│                                                                              │
│   ┌────┐ ┌────┐ ┌────┐                              ╔════════════════════╗   │
│   │ T  │ │ S1 │ │ S2 │                              ║  Conference Chat  ║   │
│   └────┘ └────┘ └────┘                              ╠════════════════════╣   │
│   ┌────┐ ┌════┐ ┌────┐                              ║                    ║   │
│   │ S3 │ │║S4 ║│ │ S5 │                              ║ [10:42] Som C.    ║   │
│   └────┘ └════┘ └────┘                              ║   "Can you share?" ║   │
│                                                     ║                    ║   │
│              (gallery dims slightly                 ║ [10:43] Teacher    ║   │
│               while sidebar open)                   ║   "On it.  Here…"  ║   │
│                                                     ║                    ║   │
│                                                     ║ [10:44] Ake R.    ║   │
│                                                     ║   "👍"  (reaction) ║   │
│                                                     ║                    ║   │
│                                                     ║                    ║   │
│                                                     ╠════════════════════╣   │
│                                                     ║ Type a message…    ║   │
│                                                     ║              [send]║   │
│                                                     ╚════════════════════╝   │
└──────────────────────────────────────────────────────────────────────────────┘

Width: 360px.  Slide animation: 200ms ease-out.
Backed by existing Phase 9.8 ChatRoom infra; ChatMessage.RoomId =
ConferenceSessionId.  No new wire codepoint.
```

---

## 11. Participants panel (More → Participants)

```
   ╔════════════════════════════════════╗
   ║  Participants (5)                  ║
   ╠════════════════════════════════════╣
   ║  👤 Teacher (You)        🎙 📷 ⋮ ║
   ║  👤 Som C.               🎙    ⋮ ║   ← ⋮ menu: Mute / Pin / Remove
   ║  👤 Ake R.   🟢 speaking 🎙 📷 ⋮ ║   ← right-click on tile = same menu
   ║  👤 Pim P. ✋ hand        🎙    ⋮ ║   ← hand-raised; teacher can Recognize
   ║  👤 Tao K. 🚫 cam off    🎙    ⋮ ║
   ╠════════════════════════════════════╣
   ║  ⚙  Mute all  /  Allow all         ║
   ║  📋 Copy join code   (deferred)    ║
   ╚════════════════════════════════════╝

Width: 320px sidebar (replaces chat sidebar when active; user toggles
between Chat and Participants via tabs at top of slide-in panel — mirrors
the existing right-rail tabs at MainWindow.xaml line 664).

Reuses MainViewModel.Students collection + the new conference indicators
(IsHandRaised, IsCamLive, IsSpeaking — IsSpeaking already exists from 13-D).
```

---

## 12. Per-tile composition (detail)

```
   ╔════════════════════════════════════════╗   ← 2px accent border when
   ║                                        ║      active speaker (green) OR
   ║                                        ║      pinned (purple)
   ║                                        ║
   ║         [WEBCAM FRAME OR                ║
   ║          PLACEHOLDER 🚫]                ║
   ║                                        ║
   ║                                        ║
   ║                                        ║
   ║                              ┌───────┐ ║   ← top-right: hand-raise
   ║                              │  ✋  │ ║      badge (yellow circle)
   ║                              └───────┘ ║
   ║ ┌─────────────────────────────┐        ║
   ║ │ Som C.  🎙 📷               │        ║   ← bottom-left: name +
   ║ └─────────────────────────────┘        ║      indicators on dark gradient
   ║                                        ║
   ╚════════════════════════════════════════╝

Indicators (always-visible on tile, bottom-left):
  🎙       mic on (unmuted, capturing)
  🎙 +     mic on + active speaker (mic icon glows green)
  🚫       mic muted (red slash overlay on 🎙)
  📷       cam on (capturing)
  🚫       cam off (red slash overlay on 📷)
  ✋       hand raised (separate badge top-right, yellow)

Reactions (floating, transient):
  👍 ❤️ 😂 😮 😢   ← 1.5 s animation: appears at tile center, drifts up + fades
                    Doesn't replace any indicator; coexists with the badges.
```

---

## 13. Layout breakpoints (responsive notes)

| N participants | Grid | Tile size (at 1366×768 viewport) | Notes |
|---|---|---|---|
| 1 | 1 centered | 640×360 | Solo "(You)" |
| 2 | 1×2 horizontal | 480×270 each | Side-by-side |
| 3 | 2 + 1 (2 above, 1 below centered) | 480×270 / 480×270 | Asymmetric |
| 4 | 2×2 | 480×270 | Full grid |
| 5–6 | 3×2 | 420×236 | Wider rows |
| 7–9 | 3×3 | 340×190 | Balanced |
| 10–12 | 4×3 | 280×158 | Wider cols |
| 13–16 | 4×4 | 280×158 | Full grid |
| 17+ | 4×4 paginated | 280×158 | Page controls |

When **pinned**: large tile = 75% width, filmstrip = 20% width with
remaining participants (scrollable column).

When **chat or participants sidebar open**: gallery shrinks to ~70%
width; layout breakpoints shift down one bucket (e.g. N=9 with sidebar
becomes 3×3 at ~260×146 instead of 340×190).

---

## 14. Self-view (PIP toggle)

Default: self appears in the gallery alongside other participants.
Optional PIP corner overlay (Meet-style):

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                                                                              │
│                                                                              │
│     [Other participants, gallery layout WITHOUT self-tile]                   │
│                                                                              │
│                                                                              │
│                                            ┌─────────────┐                   │
│                                            │  [SELF PIP] │                   │
│                                            │  192×108    │                   │
│                                            │  draggable  │                   │
│                                            └─────────────┘                   │
└──────────────────────────────────────────────────────────────────────────────┘

Toggle via ⋮ More → "Self view: gallery / PIP / hide".  Default = gallery.
```

---

## 15. Student-side ConferenceGalleryWindow differences

| Element | Teacher | Student |
|---|---|---|
| Window | Embedded `ConferenceGalleryView` in MainWindow content cell | Standalone `ConferenceGalleryWindow` (new, full-screen on spawn) |
| Header mode-toggle | Visible | **Hidden** (student doesn't choose mode) |
| End button | "📞 End Conference" → broadcasts End | **"Leave"** → sends opt-out envelope, closes window only |
| Admin controls | ⋮ More includes Mute-all, Remove, Spotlight | ⋮ More omits those entries |
| Self-label | "(You)" | "(You)" |
| Auto-end | n/a | If teacher broadcasts End → window closes automatically |
| Spawn | User clicks Conference toggle | On ConferenceStart (0x0670) dispatch in MainWindow.xaml.cs |
| Existing privacy banners | Teacher's CamLiveBanner if cam on | Student's VoiceLiveBanner if mic on + future CamLiveBanner |

---

## 16. Things deliberately NOT in mockups

- Mobile / tablet layouts — out of scope (Windows desktop).
- Dark/light theme variants — both inherit from existing theme tokens
  (`Surface.*`, `Accent.*`); same as the rest of the app.
- Animations beyond the slide-in sidebar — keep transitions minimal to
  preserve the existing app's calm feel.
- Background blur — deferred to v2 (architecture doc § 5).
- Live captions overlay — deferred to v2.
- Cross-tier diagrams — those live in
  [`conference-mode-implementation-plan.md`](./conference-mode-implementation-plan.md).
