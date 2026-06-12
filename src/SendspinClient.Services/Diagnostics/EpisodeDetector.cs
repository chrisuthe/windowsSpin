// <copyright file="EpisodeDetector.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// State machine that turns a stream of <see cref="SyncHealthSample"/> into closed
/// <see cref="EpisodeRecord"/>s. An episode opens on any audibility trigger and closes
/// after <see cref="QuietCloseSeconds"/> with no triggers, or at the <see cref="HardCapSeconds"/> cap.
/// </summary>
/// <remarks>
/// Single-threaded by contract: <see cref="Observe"/> is only called from the monitor's timer tick.
/// Triggers are audibility-anchored (see design spec): correction activity, deadband crossings,
/// callback gaps, and saturated correction rate.
/// </remarks>
public sealed class EpisodeDetector
{
    internal const double QuietCloseSeconds = 3.0;
    internal const double HardCapSeconds = 120.0;
    private const int PreRollCapacity = 100; // 10 s at 10 Hz

    private readonly double _deadbandMs;
    private readonly double _maxSpeedCorrection;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SyncHealthSample[] _preRoll = new SyncHealthSample[PreRollCapacity];
    private int _preRollCount;
    private int _preRollHead;

    private bool _hasPrev;
    private SyncHealthSample _prev;

    private bool _active;
    private Accumulator _acc;
    private long _lastTriggerMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeDetector"/> class.
    /// </summary>
    /// <param name="deadbandMs">Sync error threshold below which no episode opens (milliseconds).</param>
    /// <param name="maxSpeedCorrection">Maximum playback rate correction magnitude (e.g. 0.02 = 2%).</param>
    /// <param name="clock">Optional clock factory for testability; defaults to <see cref="DateTimeOffset.Now"/>.</param>
    public EpisodeDetector(double deadbandMs, double maxSpeedCorrection, Func<DateTimeOffset>? clock = null)
    {
        _deadbandMs = deadbandMs;
        _maxSpeedCorrection = maxSpeedCorrection;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Feeds one sample; returns a closed episode record, or null.
    /// </summary>
    /// <param name="s">The sample to observe.</param>
    /// <returns>A closed <see cref="EpisodeRecord"/> when an episode ends, otherwise null.</returns>
    public EpisodeRecord? Observe(in SyncHealthSample s)
    {
        // Pipeline restart: monotonic counters went backwards. Reset everything, emit nothing.
        if (_hasPrev && s.TotalSamplesWritten < _prev.TotalSamplesWritten)
        {
            Reset();
        }

        if (!_hasPrev)
        {
            _hasPrev = true;
            _prev = s;
            PushPreRoll(s);
            return null;
        }

        var triggered = IsTrigger(in s, in _prev);
        EpisodeRecord? closed = null;

        if (!_active && triggered)
        {
            _active = true;
            _acc = Accumulator.Open(in _prev, in s, _clock(), ComputePreRollMins());
            _lastTriggerMs = s.TimestampMs;
        }
        else if (_active)
        {
            _acc.Accumulate(in s, in _prev, _deadbandMs, _maxSpeedCorrection);
            if (triggered)
            {
                _lastTriggerMs = s.TimestampMs;
            }

            var quietSeconds = (s.TimestampMs - _lastTriggerMs) / 1000.0;
            var durationSeconds = (s.TimestampMs - _acc.StartTimestampMs) / 1000.0;
            if (quietSeconds >= QuietCloseSeconds || durationSeconds >= HardCapSeconds)
            {
                closed = _acc.Build(durationSeconds);
                _active = false;
            }
        }

        PushPreRoll(s);
        _prev = s;
        return closed;
    }

    private bool IsTrigger(in SyncHealthSample s, in SyncHealthSample prev) =>
        s.SamplesDroppedForSync > prev.SamplesDroppedForSync
        || s.SamplesInsertedForSync > prev.SamplesInsertedForSync
        || s.UnderrunCount > prev.UnderrunCount
        || s.ReanchorCount > prev.ReanchorCount
        || s.CallbackGapCount > prev.CallbackGapCount
        || Math.Abs(s.SmoothedSyncErrorMs) > _deadbandMs
        || Math.Abs(s.TargetPlaybackRate - 1.0) >= 0.9 * _maxSpeedCorrection;

    private void Reset()
    {
        _hasPrev = false;
        _active = false;
        _preRollCount = 0;
        _preRollHead = 0;
    }

    private void PushPreRoll(in SyncHealthSample s)
    {
        _preRoll[_preRollHead] = s;
        _preRollHead = (_preRollHead + 1) % PreRollCapacity;
        if (_preRollCount < PreRollCapacity)
        {
            _preRollCount++;
        }
    }

    private (double MinBufferedMs, double MaxChunkGapMs) ComputePreRollMins()
    {
        var minBuffered = double.MaxValue;
        var maxGap = 0.0;
        for (var i = 0; i < _preRollCount; i++)
        {
            ref readonly var s = ref _preRoll[i];
            if (s.BufferedMs < minBuffered)
            {
                minBuffered = s.BufferedMs;
            }

            if (s.MaxChunkGapMs > maxGap)
            {
                maxGap = s.MaxChunkGapMs;
            }
        }

        return (minBuffered == double.MaxValue ? 0 : minBuffered, maxGap);
    }

    /// <summary>Mutable episode-in-progress aggregation.</summary>
    private struct Accumulator
    {
        /// <summary>Gets or sets the timestamp (ms) when the episode started.</summary>
        public long StartTimestampMs;
        private DateTimeOffset _startedAt;
        private SyncHealthSample _baseline;     // prev sample at open: counter baseline
        private double _preRollMinBufferedMs;
        private double _preRollMaxChunkGapMs;
        private double _maxAbsSyncErrorMs;
        private double _minBufferedMs;
        private double _targetMs;
        private double _maxChunkGapMs;
        private double _maxChunkAgeMs;
        private double _maxRttJitterMs;
        private double _maxCallbackGapMs;
        private double _offsetTravelMs;
        private double _lastOffsetMs;
        private int _directionFlips;
        private int _lastDirection;             // +1 dropping, -1 inserting, 0 none yet
        private bool _rateSaturated;
        private long _endDrops, _endInserts, _endUnderruns, _endReanchors, _endCallbackGaps;
        private int _endForgetting;
        private long _bytesAtOpen, _bytesAtEnd;
        private int _sampleRate, _channels;

        /// <summary>Opens a new accumulator from baseline and first trigger sample.</summary>
        public static Accumulator Open(
            in SyncHealthSample baseline,
            in SyncHealthSample first,
            DateTimeOffset startedAt,
            (double MinBufferedMs, double MaxChunkGapMs) preRoll)
        {
            var acc = new Accumulator
            {
                StartTimestampMs = first.TimestampMs,
                _startedAt = startedAt,
                _baseline = baseline,
                _preRollMinBufferedMs = preRoll.MinBufferedMs,
                _preRollMaxChunkGapMs = preRoll.MaxChunkGapMs,
                _minBufferedMs = double.MaxValue,
                _lastOffsetMs = baseline.OffsetMs,
                _bytesAtOpen = baseline.BytesReceived,
                _sampleRate = first.SampleRate,
                _channels = first.Channels,
            };
            acc.Accumulate(in first, in baseline, deadbandMs: 0, maxSpeedCorrection: 1);
            return acc;
        }

        /// <summary>Updates aggregates from a new sample.</summary>
        public void Accumulate(in SyncHealthSample s, in SyncHealthSample prev, double deadbandMs, double maxSpeedCorrection)
        {
            _maxAbsSyncErrorMs = Math.Max(_maxAbsSyncErrorMs, Math.Abs(s.SmoothedSyncErrorMs));
            _minBufferedMs = Math.Min(_minBufferedMs, s.BufferedMs);
            _targetMs = s.TargetMs;
            _maxChunkGapMs = Math.Max(_maxChunkGapMs, s.MaxChunkGapMs);
            _maxChunkAgeMs = Math.Max(_maxChunkAgeMs, s.LastChunkAgeMs);
            _maxRttJitterMs = Math.Max(_maxRttJitterMs, s.RttJitterMs);
            _maxCallbackGapMs = Math.Max(_maxCallbackGapMs, s.MaxCallbackGapMs);
            _offsetTravelMs += Math.Abs(s.OffsetMs - _lastOffsetMs);
            _lastOffsetMs = s.OffsetMs;
            if (Math.Abs(s.TargetPlaybackRate - 1.0) >= 0.9 * maxSpeedCorrection && maxSpeedCorrection < 1)
            {
                _rateSaturated = true;
            }

            var dropped = s.SamplesDroppedForSync > prev.SamplesDroppedForSync;
            var inserted = s.SamplesInsertedForSync > prev.SamplesInsertedForSync;
            var direction = dropped ? 1 : inserted ? -1 : 0;
            if (direction != 0)
            {
                if (_lastDirection != 0 && direction != _lastDirection)
                {
                    _directionFlips++;
                }

                _lastDirection = direction;
            }

            _endDrops = s.SamplesDroppedForSync;
            _endInserts = s.SamplesInsertedForSync;
            _endUnderruns = s.UnderrunCount;
            _endReanchors = s.ReanchorCount;
            _endCallbackGaps = s.CallbackGapCount;
            _endForgetting = s.AdaptiveForgettingTriggerCount;
            _bytesAtEnd = s.BytesReceived;
        }

        /// <summary>Builds a closed <see cref="EpisodeRecord"/> from accumulated state.</summary>
        public readonly EpisodeRecord Build(double durationSeconds) => new()
        {
            StartedAt = _startedAt,
            DurationSeconds = durationSeconds,
            Drops = _endDrops - _baseline.SamplesDroppedForSync,
            Inserts = _endInserts - _baseline.SamplesInsertedForSync,
            Underruns = _endUnderruns - _baseline.UnderrunCount,
            Reanchors = _endReanchors - _baseline.ReanchorCount,
            CallbackGaps = _endCallbackGaps - _baseline.CallbackGapCount,
            AdaptiveForgettingTriggers = _endForgetting - _baseline.AdaptiveForgettingTriggerCount,
            MaxAbsSyncErrorMs = _maxAbsSyncErrorMs,
            MinBufferedMs = _minBufferedMs == double.MaxValue ? 0 : _minBufferedMs,
            TargetMs = _targetMs,
            MaxChunkGapMs = _maxChunkGapMs,
            MaxChunkAgeMs = _maxChunkAgeMs,
            MaxRttJitterMs = _maxRttJitterMs,
            MaxCallbackGapMs = _maxCallbackGapMs,
            OffsetTravelMs = _offsetTravelMs,
            DirectionFlips = _directionFlips,
            RateSaturated = _rateSaturated,
            BytesPerSecond = durationSeconds > 0 ? (_bytesAtEnd - _bytesAtOpen) / durationSeconds : 0,
            PreRollMinBufferedMs = _preRollMinBufferedMs,
            PreRollMaxChunkGapMs = _preRollMaxChunkGapMs,
            SampleRate = _sampleRate,
            Channels = _channels,
        };
    }
}
