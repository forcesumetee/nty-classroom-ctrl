using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Timer = System.Timers.Timer;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 4 Part 5: Aggregates per-student quality reports and chooses a single
/// broadcast bitrate driven by the slowest receiver ("lowest common denominator").
///
/// Strategy:
///   • Ramp DOWN immediately (×0.8) when any student's score drops below 70.
///   • Ramp UP slowly (×1.1) only after 3 consecutive evaluation cycles where
///     every active student is above 90 — hysteresis to avoid oscillation.
///   • Bounded to [<see cref="MinBitrate"/>, <see cref="MaxBitrate"/>],
///     starting at <see cref="DefaultBitrate"/>.
///   • New students are ignored for <see cref="GracePeriodMs"/> (10 s) so their
///     "no data yet" state doesn't drag the bitrate down.
///
/// Resolution and FPS are NOT changed — only bitrate. Resolution flips would
/// be visually jarring; bitrate changes are smooth.
/// </summary>
public sealed class AdaptiveBitrateController : IDisposable
{
    public const int MinBitrate         = 200_000;
    public const int MaxBitrate         = 2_000_000;
    public const int DefaultBitrate     =   500_000;
    public const int EvaluationIntervalMs = 5_000;
    public const int GracePeriodMs        = 10_000;
    public const int StaleReportThresholdMs = 15_000;

    private readonly ILogger<AdaptiveBitrateController> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ReportEntry> _reports = new();
    private readonly Timer _evalTimer;

    private int _currentBitrate = DefaultBitrate;
    private int _consecutiveGoodCycles;

    /// <summary>Currently chosen bitrate (in bps). Read by ScreenBroadcaster on Start.</summary>
    public int CurrentBitrateBps
    {
        get { lock (_lock) { return _currentBitrate; } }
    }

    /// <summary>When false, evaluation runs but bitrate never changes (UI toggle).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fired whenever the chosen bitrate changes. Listener should re-tune the encoder.</summary>
    public event EventHandler<BitrateChangedEventArgs>? BitrateChanged;

    public AdaptiveBitrateController(ILogger<AdaptiveBitrateController> logger)
    {
        _logger = logger;
        _evalTimer = new Timer(EvaluationIntervalMs) { AutoReset = true };
        _evalTimer.Elapsed += (_, _) => Evaluate();
        _evalTimer.Start();
    }

    public void OnReportReceived(Guid studentId, ScreenStreamQualityReportMessage report)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (!_reports.TryGetValue(studentId, out var entry))
            {
                entry = new ReportEntry { FirstSeenUtcMs = nowMs };
                _reports[studentId] = entry;
            }
            entry.LatestScore = report.QualityScore;
            entry.LatestReportUtcMs = nowMs;
        }
    }

    public void RemoveStudent(Guid studentId)
    {
        lock (_lock)
        {
            _reports.Remove(studentId);
        }
    }

    private void Evaluate()
    {
        if (!Enabled) return;

        int? minScore = null;
        int countAboveGood = 0;
        int countTotal = 0;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_lock)
        {
            foreach (var (_, entry) in _reports)
            {
                if (nowMs - entry.FirstSeenUtcMs < GracePeriodMs) continue;
                if (nowMs - entry.LatestReportUtcMs > StaleReportThresholdMs) continue;

                countTotal++;
                if (minScore == null || entry.LatestScore < minScore.Value)
                    minScore = entry.LatestScore;
                if (entry.LatestScore > 90) countAboveGood++;
            }
        }

        if (minScore == null) return; // no eligible reports — keep current bitrate

        int newBitrate = _currentBitrate;
        string? reason = null;

        if (minScore < 70)
        {
            newBitrate = Math.Max(MinBitrate, (int)(_currentBitrate * 0.8));
            _consecutiveGoodCycles = 0;
            reason = $"min score {minScore} < 70 → ramp down";
        }
        else if (countTotal > 0 && countAboveGood == countTotal)
        {
            _consecutiveGoodCycles++;
            if (_consecutiveGoodCycles >= 3)
            {
                newBitrate = Math.Min(MaxBitrate, (int)(_currentBitrate * 1.1));
                _consecutiveGoodCycles = 0;
                reason = "all above 90 for 3 cycles → ramp up";
            }
        }
        else
        {
            _consecutiveGoodCycles = 0;
        }

        if (newBitrate != _currentBitrate)
        {
            var oldBitrate = _currentBitrate;
            lock (_lock) { _currentBitrate = newBitrate; }
            _logger.LogInformation("Adaptive bitrate: {Old} → {New} bps ({Reason}, n={Count})",
                oldBitrate, newBitrate, reason, countTotal);
            BitrateChanged?.Invoke(this, new BitrateChangedEventArgs(newBitrate, reason ?? ""));
        }
    }

    public void Dispose()
    {
        _evalTimer.Stop();
        _evalTimer.Dispose();
    }

    private sealed class ReportEntry
    {
        public int LatestScore;
        public long FirstSeenUtcMs;
        public long LatestReportUtcMs;
    }
}

public sealed class BitrateChangedEventArgs : EventArgs
{
    public int NewBitrateBps { get; }
    public string Reason { get; }
    public BitrateChangedEventArgs(int bps, string reason)
    {
        NewBitrateBps = bps;
        Reason = reason;
    }
}
