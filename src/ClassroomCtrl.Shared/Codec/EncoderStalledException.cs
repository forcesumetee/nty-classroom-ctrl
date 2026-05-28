using System;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc4 — thrown by an <see cref="IVideoEncoder"/> implementation
/// that detects it has consumed N consecutive input frames without producing
/// any output, where N is large enough to rule out normal warm-up or transient
/// frame-skip.  Caught by both broadcasters' encode loops and used as the
/// signal to dispose the failed encoder and rebuild via
/// <see cref="VideoEncoderFactory"/> with <c>useHardwareH264:false</c> — the
/// same wire codec, just routed to the validated OpenH264 SW path.
///
/// This exists primarily because of the round-3 finding: the AMD H.264 MFT
/// on the dev box enters a state after <c>NotifyBeginStreaming</c> where it
/// raises only an InputStreamStateChanged event and refuses every
/// <c>ProcessInput</c> with MF_E_NOTACCEPTING.  No exception was thrown at
/// init — the encoder LOOKED healthy — so the broadcaster sat there feeding
/// it indefinitely.  Throwing from <c>Encode()</c> after the stall is
/// detectable gives the broadcaster a clean signal to fall back without
/// blanking the share.
///
/// The same guard also protects against analogous vendor quirks on customer
/// Intel Quick Sync / NVENC hardware that may surface only on 2-PC validation.
/// </summary>
public sealed class EncoderStalledException : Exception
{
    /// <summary>Number of consecutive Encode() calls that produced no output
    /// before the throw fired.  Logged by the broadcaster so the dev can
    /// distinguish a "never started" stall from a "stopped mid-stream" stall.</summary>
    public int ConsecutiveNullReturns { get; }

    /// <summary>Whether the encoder ever produced output before stalling.
    /// false = the MFT never accepted input at all (round-3 AMD pattern).
    /// true  = the MFT was working then jammed.</summary>
    public bool HadOutputBeforeStall { get; }

    public EncoderStalledException(string message, int consecutiveNullReturns, bool hadOutputBeforeStall)
        : base(message)
    {
        ConsecutiveNullReturns = consecutiveNullReturns;
        HadOutputBeforeStall   = hadOutputBeforeStall;
    }
}
