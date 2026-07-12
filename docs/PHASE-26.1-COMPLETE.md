# Phase 26.1 COMPLETE — v1.2.1 Bulk-Lock Burst Fix (RELEASE-CLEAN)

**Status:** ✅ merged · tagged · versioned · Windows-verified · **SHIP-READY**.
**Release commit:** `ed51ec0` (`v1.2-multiselect` HEAD = tag `v1.2.1` = `<Version>1.2.1</Version>`).
**Date:** 2026-07-13. Installer build = a separate follow-up session.

---

## Execution summary (external Windows track)
A customer-reported reliability bug fixed on the shipped Windows product, in parallel with
the macOS/Avalonia port (which stayed on `avalonia-experiment`, untouched).

| Sub-phase | Commit | Delivered |
|---|---|---|
| 26.1-A | `0db139e` | Reliable-path fix: `reliable` param on 6 targeted-send methods + bulk commands opt in & await; fire-and-forget policy bug fixed |
| 26.1-B | `da68632` | `tools/BulkDispatchTest` — loopback reproduction + deterministic N/N fix proof |
| 26.1-C | `c393db6` | Live "Sending i of N…" progress feedback |
| 26.1-D | `e072a22` | Findings doc — root-cause correction + teachable principle |
| 26.1-E | `40fd26e` | Windows verification (3/3 PASS) |
| 26.1-F | `eaf7f01` | Strip CLAUDE.md → pure-patch diff |
| merge  | `0a75685` | `--no-ff` merge into `v1.2-multiselect` |
| 26.1-G | `ed51ec0` | Version bump → 1.2.1 (tag repositioned here) |

## Root cause + fix (one paragraph)
Targeted control ops fan out to every peer (+ receiver-side `IsForMe`) and all rode the
lossy `_outbox` (cap 16, `DropOldest`). A bulk action over N students = N broadcast frames
bursting into each peer's cap-16 queue faster than TCP drain → ~34 dropped → ~18/50 locked.
**Not** a network/UDP issue. Fix = route bulk commands through the existing
`BroadcastReliableAsync` (`FullMode.Wait`) so the producer back-pressures at drain rate → 0
drops. Wire unchanged. See `PHASE-26.1-FINDINGS.md` for the full narrative + the codified
diagnostic principle ("when the fix is 'add a delay', check for a bypassed back-pressure
path — look at ChannelWriter/FullMode, not netstat").

## Test verification (Windows, 2026-07-13)
| Test | Result |
|---|---|
| `dotnet build` (full WPF solution) | ✅ **0 errors** (NU1902 = expected MessagePack-pin advisory) |
| `tools/BulkDispatchTest` (2/20/50) | ✅ reliable **2/2 · 20/20 · 50/50**; lossy reproduced **23/50** (customer field: 18/50) |
| T1–T26 wire compat | ✅ **26/26 PASS** (round-trip + backward + forward compat) |

## Release state — everything aligned at `ed51ec0`
- Branch `v1.2-multiselect` HEAD = `ed51ec0` (synced with origin).
- Tag `v1.2.1` → `ed51ec0` (stamps `<Version>1.2.1</Version>`).
- Feature branch `v1.2.1-bulk-lock-fix` deleted (merged; history preserved via `--no-ff`).
- Patch diff = **8 files, fix-only** (no CLAUDE.md).

## Installer build steps (preserved for next session)
1. **Checkout the release:** `git checkout v1.2.1` (or build `v1.2-multiselect` @ `ed51ec0`).
2. **Release build** (Windows): `dotnet build -c Release` — or the Teacher publish profile.
   Expect 0 errors. **Do NOT "fix" NU1902/NU1903** — MessagePack is pinned at 2.5.187 for
   wire byte-compat (Phase 24.1).
3. **Package** via the existing installer pipeline as a **teacher-side patch** (v1.2 → v1.2.1).
   Assemblies now stamp 1.2.1.
4. **No student rollout** — the fix is entirely teacher-side; the wire is unchanged; existing
   v1.2 student agents interoperate untouched.

## Customer ship checklist
- [ ] Build Release from tag `v1.2.1` (`ed51ec0`).
- [ ] Confirm built assembly version reads **1.2.1**.
- [ ] Package teacher-side patch installer.
- [ ] **Smoke test:** real 50-endpoint bulk-lock on the hotspot → expect **50/50** (ship gate).
- [ ] Spot-check other bulk actions (unlock / policy / mute / logoff) still work.
- [ ] Release note: *"v1.2.1 — resolves bulk-lock reliability at scale (50/50); teacher-side
      patch, no student update required."*
- [ ] Deliver installer to customer; confirm the 50-endpoint scenario on their site.

## The teachable win
The original hypothesis ("UDP burst → add `Task.Delay(10)`") would have masked the symptom
with an arbitrary timer while leaving a lossy channel in place. The real fix used
infrastructure the codebase **already had** for exactly this (must-arrive traffic). Diagnosis
beat guessing; the principle is now codified for the next debugger.
