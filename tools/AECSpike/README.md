# AEC Spike — Phase 13-D Step 0

Hard-gate spike for Tier 3 voice chat. Verifies the technical path Tier 3's
`StudentMicBroadcaster` would use to engage Windows' built-in AEC (Layer 2 of
the 3-layer strategy in [`docs/breakout-rooms-tier3-design.md`](../../docs/breakout-rooms-tier3-design.md) §5).

## What this tool verifies *programmatically*

1. **Capture endpoint exists for the Communications role.** Tier 3 targets this
   endpoint specifically because Windows applies AEC + noise suppression to
   capture from Communications-role devices on supported drivers.
2. **`WasapiCapture` opens it.** Same API path the Tier 3 broadcaster would use.
3. **Negotiated capture format.** Reveals whether the broadcaster needs a
   resample stage (Tier 3 target = 16 kHz mono 16-bit).
4. **Per-100 ms RMS over a 5-second window.** Provides a quantitative gauge
   the dev compares between two runs (silent speakers vs loud speakers) to
   reach the AEC verdict.

## What this tool does NOT verify

- The Windows audio engine never exposes "is the AEC effect enabled on this
  endpoint?" as a boolean via NAudio. The AEC presence verdict is the dev's
  subjective + RMS-based call after running the comparison below.
- The actual subjective listen-test (mic feedback into speakers → howl /
  attenuated / silent).

## How to run

```pwsh
dotnet run --project tools/AECSpike -c Release
```

The tool prints the device enumeration and capture-format negotiation
unconditionally, then waits at `Press Enter to start the run …` so the dev can
get the testbed ready (open a media player for the loopback run, etc.).

## Dev procedure — two-run comparison

### Run 1 — BASELINE (speakers silent)

1. Run the tool.
2. At the "Press Enter to start" prompt, **stop all audio playback** in other
   apps. Speakers must be silent during this run.
3. Press Enter and **speak normally** into the mic for the 5-second capture
   (count "one two three four five" at a steady volume).
4. Save the printed `Summary:` line.

### Run 2 — LOOPBACK (speakers playing loud)

1. Run the tool again.
2. At the prompt, **start loud audio playback** in another app (any audio at
   ~70-80% volume — classroom-realistic loud).
3. Press Enter and **speak the same phrase at the same volume** into the mic.
4. Save the printed `Summary:` line.

### Verdict

Compare LOOPBACK mean RMS to BASELINE mean RMS:

| Δ (LOOPBACK − BASELINE) | Verdict | Action |
|---|---|---|
| ≤ +6 dBFS | **AEC WORKS** | Proceed with full Tier 3 plan. Step 3 enables WASAPI AEC by default. |
| +6 to +15 dBFS | **AEC PARTIAL** | Proceed; Step 3 enables AEC default + ship "USB headset recommended" doc. |
| > +15 dBFS | **AEC FAILS** | STOP and discuss the three options in the Phase 13-D primer (Step 0 § "Decision gate"): library AEC, PTT-only with USB headset, defer Tier 3. |

## Result from this dev box

Dev box ran with default endpoint configuration (USB headset connected, Realtek
built-in mic array also present):

- **Communications capture endpoint:** `Microphone (AB13X USB Audio)`
  — `{0.0.1.00000000}.{8896323b-d8f9-43bc-9249-c90ceddbbc76}`
- **Communications + Console roles map to the SAME device.** Typical
  laptop/desktop topology.
- **Other active capture endpoint observed:** `Microphone Array (Realtek(R) Audio)`
  — would be the default if the USB headset were unplugged.
- **Negotiated capture format:** **48 kHz, 2 channels, 32-bit IEEE float.**

### Format note for Step 3

The negotiated format on this box is 48k stereo float. Tier 3 target is 16 kHz
mono 16-bit PCM. The `StudentMicBroadcaster` (Step 3) **must include a
resample + downmix + sample-format convert stage** before serialization.
Recommended: `NAudio.Wave.MediaFoundationResampler` or `WdlResamplingSampleProvider`
+ explicit `ToMonoSampleProvider` + `Wave16ToSampleProvider`/inverse.

### Subjective listen-test verdict

**To be filled in by the dev.**  Run the two-pass comparison above and paste
the BASELINE/LOOPBACK summaries here, plus the verdict (WORKS / PARTIAL / FAILS).

```
BASELINE Summary:  mean=___ dBFS  peak=___ dBFS  windows=___
LOOPBACK Summary:  mean=___ dBFS  peak=___ dBFS  windows=___
Δ mean:            ___ dBFS
Verdict:           WORKS / PARTIAL / FAILS
Hardware notes:    (mic + speaker model, Win version, driver age)
```

## What to do after Step 0

- **WORKS:** Resume Phase 13-D primer at Step 1.
- **PARTIAL:** Resume at Step 1; Step 3 still enables AEC by default; ship
  notes flag USB-headset recommendation.
- **FAILS:** STOP. The primer's Step 0 § "Decision gate" lists three options
  (library AEC / PTT-only with USB headset / defer Tier 3). Discuss with dev
  before any Tier 3 code lands.
