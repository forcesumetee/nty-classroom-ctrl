# Session Summary — 2026-07-13 (Session 2: v1.2.1 Windows patch)

Second track of a two-track working day. Session 1 (macOS/Avalonia port) is summarized in
the **other** repo at `~/Dev/nty-classroom-avalonia/docs/SESSION-2026-07-12-SUMMARY.md`
(Milestone 15 LIVE-CONFIRMED). Session 2 (this summary) switched to the **shipped Windows
product** to fix a customer-reported bug — a fully separate track that never touched the
macOS work.

**Repo:** `~/Dev/nty-classroom-macos/` (the shipped Windows codebase). Branch:
`v1.2-multiselect` (the shipping branch). Remote `origin`:
github.com/forcesumetee/nty-classroom-ctrl.

---

## Phase 26.1 — v1.2.1 Bulk-Lock Burst Fix ✅ SHIP-READY

**Customer bug:** 50 endpoints, Ctrl+A → Bulk Lock → only **~18/50** locked (36%).
**Result:** fixed, verified **50/50**, merged, tagged `v1.2.1`, versioned — release-clean at
`ed51ec0`.

- **Root cause (diagnosed, not guessed):** targeted control ops broadcast to every peer
  (+ receiver `IsForMe`) on the lossy `_outbox` (cap 16, `DropOldest`). N bulk sends = N
  frames bursting past the cap-16 queue → ~34 dropped. **Not** UDP/network — an internal
  bounded Channel.
- **Fix:** route bulk commands through the existing `BroadcastReliableAsync` (`FullMode.Wait`)
  → back-pressure at TCP-drain rate → 0 drops. Wire unchanged (T1-T26 intact). Default
  `reliable=false` keeps single-target callers byte-for-byte unchanged.
- **Bonus bug:** `BulkApplyPolicy`/`BulkRevertPolicy` were fire-and-forget (`_ =`) —
  silent failures; now awaited + error-surfaced.
- **UI:** live "Sending i of N…" progress.
- **Test without 50 PCs:** `tools/BulkDispatchTest` loopback harness — reliable N/N asserted;
  lossy reproduced 23/50 (mirrors the field's 18/50).

See `docs/PHASE-26.1-COMPLETE.md` (release state + installer steps + ship checklist) and
`PHASE-26.1-FINDINGS.md` (full diagnosis + the codified diagnostic principle).

## Commits (Session 2) — 7 + merge
```
ed51ec0  26.1-G: bump product version to 1.2.1                 ← v1.2-multiselect HEAD, tag v1.2.1
0a75685  Merge v1.2.1-bulk-lock-fix into v1.2-multiselect (--no-ff)
eaf7f01  26.1-F: strip CLAUDE.md from v1.2.1 patch branch
40fd26e  26.1-E: v1.2.1 Windows verification COMPLETE
e072a22  26.1-D: findings — root-cause correction + principle
c393db6  26.1-C: bulk-action UI feedback (live progress)
da68632  26.1-B: BulkDispatchTest — loopback reproduction + fix proof
0db139e  26.1-A: bulk-action burst fix — route to reliable path
```

## Verification (Windows, 2026-07-13)
| Test | Result |
|---|---|
| `dotnet build` (WPF solution) | ✅ 0 errors |
| `BulkDispatchTest` 2/20/50 | ✅ reliable 2/2 · 20/20 · **50/50** (lossy 23/50 reproduced the bug) |
| T1–T26 wire compat | ✅ 26/26 PASS |

## Release state (this repo)
- `v1.2-multiselect` HEAD = **`ed51ec0`** (synced with origin).
- Tag **`v1.2.1`** → `ed51ec0` (stamps `<Version>1.2.1</Version>`).
- Feature branch `v1.2.1-bulk-lock-fix` deleted (merged; `--no-ff` preserved history).
- `CLAUDE.md` remains present-but-untracked (the macOS-port guidance; kept out of the patch).

---

## Combined day (Session 1 + Session 2) — both tracks advanced
| Track | Repo / branch | Outcome | Head |
|---|---|---|---|
| **1 — macOS port** | `nty-classroom-avalonia` / `avalonia-experiment` | Phases 25.7 + 26.0 → **Milestone 15 LIVE-CONFIRMED** (Mac student live in Windows Teacher, 4/4) | `df4d683` |
| **2 — Windows patch** | `nty-classroom-macos` / `v1.2-multiselect` | **v1.2.1 bulk-lock fix SHIP-READY** (50/50) | `ed51ec0` (tag `v1.2.1`) |

Shipped-product bug fixed **and** the macOS port proven live — same day, non-interfering
tracks. The macOS repo constraint held: Session 2's Windows work is on the shipped repo's
own branch; the Avalonia experiment was never touched.

## Next-session priority queue
1. **Build v1.2.1 installer + smoke test + ship customer** (~1 h, Windows) — completes the
   customer commitment. Recipe: `git checkout v1.2.1` → `dotnet build -c Release` → package
   teacher-side patch → 50-endpoint smoke test (50/50) → deliver.
2. **Phase 27 native APIs — ScreenCaptureKit** (Mac, ~3–5 h/subsystem) — highest-leverage
   macOS momentum; the Teacher already sends the stream request (proven LIVE, currently
   deferred).
3. **Progressive UI ports + polish** (Mac) — steady mechanical progression on the proven
   design system.

## Team handoff
- **v1.2.1 = shipping-ready code.** Recipe: checkout tag `v1.2.1` → build Release → package
  → smoke test (50/50) → ship. No student rollout.
- **macOS foundation LIVE-proven** (Milestone 15); Phase 27 native-API path is clear.
- Both tracks are green, synced with origin, and independently documented.
