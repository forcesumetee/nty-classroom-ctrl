# Conference Parity — Implementation Plan (Phase 16-B through E)

**Status:** investigation (Phase 16-A). Cross-tier sequencing + validation
gates + rollback criteria.
**Parent:** [`conference-parity-architecture.md`](./conference-parity-architecture.md).
**Tier siblings:** [`conference-parity-tier1-design.md`](./conference-parity-tier1-design.md),
[`conference-parity-tier2-design.md`](./conference-parity-tier2-design.md),
[`conference-parity-tier3-design.md`](./conference-parity-tier3-design.md).
**Last updated:** 2026-05-29.

---

## Cross-tier sequencing

```
Phase 16-A  →  4-5 design docs (this round)                  ~45-60 min   investigation
Phase 16-B  →  Shared.Wpf + UI bug fix + student gallery    ~2-3 hr       MVP gate (foundation)
Phase 16-C  →  Peer cam routing                              ~1-2 hr       MVP feature parity
Phase 16-D  →  Host vs Participant role + permissions        ~1 hr         ship-ready
Phase 16-E  →  Polish (badges, empty states, Spotlight)      ~30 min       optional
```

Total MVP (B + C + D) = **~4-6 hr Claude Code + 30-45 min dev validation**.

## Dependency graph

```
16-B  (Shared.Wpf foundation + UI bug fix)
  │
  ├──→ 16-C  (peer cam routing — requires student gallery surface)
  │
  ├──→ 16-D  (role gating — requires unified shell VM interface)
  │
  └──→ 16-E  (polish, optional)

Recommended order: B → C → D.
B/C/D are independent enough that C and D could parallelize after B,
but C provides real peer-cam frames to test the role-gated UI against
in D's acceptance, so C-then-D is the safe sequence.
```

## Per-phase validation gates

### After 16-B (foundation gate)

| Gate | Acceptance |
|---|---|
| Build clean | All 3 + Shared.Wpf compile, 0 errors |
| Wire-compat | T1-T16 PASS (no wire changes) |
| Visual smoke | Teacher gallery shows N=2 tiles side-by-side (UI bug FIXED) |
| Visual smoke | Student window hosts real gallery (multi-tile-capable) |
| Visual smoke | Student Leave bar no longer overlays the tile area |
| Regression | Classroom mode toggle round-trip clean |
| Regression | Existing 14-B teacher cam works |
| Regression | Existing 15-D / 15-E features (toolbar / sidebar / Recognize / reactions) work |

**Gate fail → rollback:** revert the 16-B commit chain; document the
blocking issue in `docs/conference-parity-architecture.md` § Risks for
the next attempt. 16-C/D blocked until 16-B clean.

### After 16-C (feature parity gate)

| Gate | Acceptance |
|---|---|
| Build clean | All 3 + Shared.Wpf, 0 errors |
| Wire-compat | T1-T19 PASS (T17-T19 NEW) |
| Visual smoke | Teacher cam visible on student tile (16-C 0x0681 path) |
| Visual smoke | Student cam visible on teacher tile (NEW) |
| 3-PC ideal | Two students see each other's cams |
| Bandwidth | Wireshark egress for 3-PC < 2 Mbps comfortable |
| Regression | 9.5 Classroom CameraBroadcast (0x0460-0x0462) still works |
| Regression | Mode toggle Classroom↔Conference doesn't leak cam state |

**Gate fail → rollback:** revert the 16-C commit chain; foundation
(16-B) stays. UI parity ships without peer cam — degraded but not
broken.

### After 16-D (ship-ready gate)

| Gate | Acceptance |
|---|---|
| Build clean | All 3 + Shared.Wpf, 0 errors |
| Wire-compat | T1-T19 PASS (no new wire) |
| Visual smoke | Student toolbar hides admin actions |
| Visual smoke | Teacher's End-button shows "End"; student's shows "Leave" |
| Visual smoke | Mute Others (host) → student mic mutes via 13-D 0x0641 |
| Manual | Leave (student) closes own window; teacher session continues |
| Regression | 15-E Recognize / Reactions still work |

**Gate fail → rollback:** revert 16-D commit chain; foundation +
peer cam (16-B + C) ship — gives "Conference works but UI not yet
role-gated." Customer-visible degraded but not blocking.

## Expected commit chain

### Phase 16-B (8 commits, ~2-3 hr)

```
Phase 16-B step 1: ClassroomCtrl.Shared.Wpf project skeleton
Phase 16-B step 2: move converters to Shared.Wpf + Teacher shims
Phase 16-B step 3: move ConferenceTile + Gallery view-models
Phase 16-B step 4: move Conference UI controls to Shared.Wpf
Phase 16-B step 5: fix gallery UniformGrid binding (ColumnsForPage VM)
Phase 16-B step 6: Student.Agent Leave bar Grid.Row typo
Phase 16-B step 7: Student.Agent hosts Shared.Wpf gallery + shell VM
Phase 16-B step 8: build + 8-item 2-PC acceptance
```

### Phase 16-C (5-6 commits, ~1-2 hr)

```
Phase 16-C step 1: wire protocol (0x0680-0x0682) + T17-T19
Phase 16-C step 2: teacher relay + receive arms
Phase 16-C step 3: Student.Agent StudentCameraBroadcaster
Phase 16-C step 4: teacher cam mode-aware routing
Phase 16-C step 5: receive routing + self-loopback + per-tile dispatch
Phase 16-C step 6: build + 3-PC ideal smoke (2-PC fallback)
```

### Phase 16-D (4-5 commits, ~1 hr)

```
Phase 16-D step 1: IConferenceRole + IConferenceShellViewModel
Phase 16-D step 2: MuteParticipantCommand reuses 13-D MicMuteRequest
Phase 16-D step 3: toolbar + sidebar role-gated visibility
Phase 16-D step 4: localization (EN+TH) + role labels
Phase 16-D step 5: build + 2-PC acceptance
```

### Total

~17-19 commits across 3 phases. After 16-D, Conference Mode is
shippable at v1.x for class sizes ≤ 15 students on gigabit.

## Per-step commit discipline

Following the 13-A → 15-E rhythm:
- Small, reversible, build-clean per step.
- Mental walk-through before committing.
- Each step ends with a focused diff (file count keeps low, except
  16-B step 4 which moves 4 XAML files at once — atomic move is
  better than partial).
- Never skip git hooks; never bypass signing.

## Risk-rollback decision tree

```
                     16-B 8/8 PASS?
                          │
                ┌─── yes ─┼─── no ────→ revert 16-B; flag blockers
                │
            16-C 8/8 PASS?
                │
       ┌── yes ─┼── partial (≥6/8) ──→ ship 16-B only; revisit 16-C
       │        │
       │        └── < 6/8 ─→ revert 16-C; ship 16-B
       │
   16-D 6/6 PASS?
       │
       ├── yes ─→ ship v1.x with 16-B+C+D
       │
       └── < 5/6 ─→ ship v1.x with 16-B+C; defer 16-D to next round
```

Worst case: 16-B alone ships an UI-bug-fixed Conference with single-tile
student window (status quo from 15-E but bug-free). Better case: 16-B+C
ships peer cam without role-gating (visible-but-clickable admin
buttons on student — annoying but functional). Best case: full
16-B+C+D ships v1.x.

## After 16-D — what's left for v2

| Item | Why deferred |
|---|---|
| **15-F SFU** | Scale trigger: customer hits >20 simultaneous Conference participants with bandwidth pressure |
| **Co-host pattern** | v2 polish; HostRole alone covers 1-teacher MVP |
| **Recording** | v2; needs storage policy + PDPA review |
| **Background blur** | v2; MF segmentation heavy |
| **Live captions** | v2; STT heavy |
| **Force-cam by host** | v2; PDPA-sensitive (forced cam-on without consent) |
| **Pin-for-All / Spotlight** | 16-E (optional this round) or defer to v2 |

## Cumulative design-doc set after 16-A

```
docs/
├── breakout-rooms-architecture.md           (13-A)
├── breakout-rooms-tier1-design.md           (13-A)
├── breakout-rooms-tier2-design.md           (13-A)
├── breakout-rooms-tier3-design.md           (13-A)
├── conference-mode-architecture.md          (14-A)
├── conference-mode-tier1-design.md          (14-A)
├── conference-mode-tier2-design.md          (14-A)
├── conference-mode-tier3-design.md          (14-A)
├── conference-mode-ux-architecture.md       (15-A)
├── conference-mode-ui-mockups.md            (15-A)
├── conference-mode-implementation-plan.md   (15-A)
├── conference-parity-architecture.md        (16-A) ← this round
├── conference-parity-tier1-design.md        (16-A) ← this round
├── conference-parity-tier2-design.md        (16-A) ← this round
├── conference-parity-tier3-design.md        (16-A) ← this round
└── conference-parity-implementation-plan.md (16-A) ← this round
```

16 cumulative docs / ~280 KB. Investigation-first discipline pays off:
3 of 3 prior investigations (13-A, 14-A, 15-A) found foundation already
in place. 16-A flags 1 confirmed bug + 1 secondary bug + 12 capability
gaps + new architecture for the parity gap.
