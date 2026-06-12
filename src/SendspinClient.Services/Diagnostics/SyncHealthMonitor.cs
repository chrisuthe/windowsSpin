// <copyright file="SyncHealthMonitor.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Synchronization;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Always-on sync-health watchdog: samples pipeline + clock stats at 10 Hz, detects trouble
/// episodes, classifies them, and writes them to the dedicated sync-health log.
/// </summary>
/// <remarks>
/// Never affects playback: every tick is wrapped, failures log once and degrade to inert.
/// The latest verdict is exposed for the Stats window via <see cref="HealthDisplay"/>.
/// </remarks>
public sealed class SyncHealthMonitor : IDisposable
{
    private const int SampleIntervalMs = 100;

    // Matches SyncCorrectionOptions defaults used by the pipeline (syncOptions: null → SDK Default).
    private const double DeadbandMs = 2.0;
    private const double MaxSpeedCorrection = 0.02;

    private readonly IAudioPipeline _pipeline;
    private readonly IClockSynchronizer _clockSync;
    private readonly ReadCallbackGapTracker _gapTracker;
    private readonly SyncHealthLog _log;
    private readonly ILogger<SyncHealthMonitor> _logger;
    private readonly EpisodeDetector _detector = new(DeadbandMs, MaxSpeedCorrection);
    private readonly Timer _timer;

    private bool _wasActive;
    private bool _tickFaulted;
    private int _episodeCount;
    private volatile string _healthDisplay = "No issues detected";

    /// <summary>Gets the latest verdict line for the Stats window (e.g. "Network starvation suspected (2 episodes)").</summary>
    public string HealthDisplay => _healthDisplay;

    /// <summary>Gets the number of episodes recorded this session.</summary>
    public int EpisodeCount => _episodeCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHealthMonitor"/> class.
    /// </summary>
    /// <param name="pipeline">The audio pipeline to monitor.</param>
    /// <param name="clockSync">The clock synchronizer to sample.</param>
    /// <param name="gapTracker">The read-callback gap tracker for audio-thread starvation diagnostics.</param>
    /// <param name="log">The sync-health log writer.</param>
    /// <param name="logger">Logger for episode warnings and fault reporting.</param>
    public SyncHealthMonitor(
        IAudioPipeline pipeline,
        IClockSynchronizer clockSync,
        ReadCallbackGapTracker gapTracker,
        SyncHealthLog log,
        ILogger<SyncHealthMonitor> logger)
    {
        _pipeline = pipeline;
        _clockSync = clockSync;
        _gapTracker = gapTracker;
        _log = log;
        _logger = logger;
        _timer = new Timer(OnTick, state: null, dueTime: SampleIntervalMs, period: SampleIntervalMs);
    }

    private void OnTick(object? state)
    {
        try
        {
            var stats = _pipeline.BufferStats;
            if (stats is null)
            {
                _wasActive = false;
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                WriteSessionHeader();
            }

            var clock = _clockSync.GetStatus();
            var format = _pipeline.OutputFormat;
            var sample = new SyncHealthSample
            {
                TimestampMs = Environment.TickCount64,
                SmoothedSyncErrorMs = stats.SmoothedSyncErrorMs,
                BufferedMs = stats.BufferedMs,
                TargetMs = stats.TargetMs,
                UnderrunCount = stats.UnderrunCount,
                SamplesDroppedForSync = stats.SamplesDroppedForSync,
                SamplesInsertedForSync = stats.SamplesInsertedForSync,
                ReanchorCount = stats.ReanchorCount,
                TargetPlaybackRate = stats.TargetPlaybackRate,
                TotalSamplesWritten = stats.TotalSamplesWritten,
                LastChunkAgeMs = stats.LastChunkAgeMs,
                MaxChunkGapMs = stats.MaxChunkGapMs,
                ChunkJitterMs = stats.ChunkJitterMs,
                BytesReceived = stats.BytesReceived,
                OffsetMs = clock.OffsetMilliseconds,
                RttJitterMs = clock.RttJitterMicroseconds / 1000.0,
                AdaptiveForgettingTriggerCount = clock.AdaptiveForgettingTriggerCount,
                CallbackGapCount = _gapTracker.GapCount,
                MaxCallbackGapMs = _gapTracker.MaxGapMs,
                SampleRate = format?.SampleRate ?? 48000,
                Channels = format?.Channels ?? 2,
            };

            if (_detector.Observe(in sample) is { } episode)
            {
                var classification = EpisodeClassifier.Classify(episode);
                _log.WriteEpisode(episode, classification);
                var count = Interlocked.Increment(ref _episodeCount);
                _healthDisplay = $"{Describe(classification)} ({count} episode{(count == 1 ? string.Empty : "s")})";
                _logger.LogWarning(
                    "Sync health episode: {Verdict} duration={Duration:F1}s evidence={Evidence}",
                    classification.Verdict, episode.DurationSeconds, classification.Evidence);
            }
        }
        catch (Exception ex)
        {
            if (!_tickFaulted)
            {
                _tickFaulted = true;
                _logger.LogError(ex, "Sync health monitor tick failed; diagnostics degraded");
            }
        }
    }

    private void WriteSessionHeader()
    {
        var format = _pipeline.OutputFormat;
        var version = typeof(SyncHealthMonitor).Assembly.GetName().Version?.ToString(3) ?? "?";
        _log.WriteSessionHeader(
            $"app={version} os={Environment.OSVersion.VersionString} " +
            $"format={format?.SampleRate}Hz/{format?.Channels}ch " +
            $"outputLatency={_pipeline.DetectedOutputLatencyMs}ms");
    }

    private static string Describe(SyncHealthClassification c) => c.Verdict switch
    {
        SyncHealthVerdict.NetworkStarvation => "Network starvation suspected",
        SyncHealthVerdict.ClockSyncInstability => "Clock sync instability suspected",
        SyncHealthVerdict.DeviceClockSkew => c.EstimatedSkewPpm is { } ppm
            ? $"Device clock skew suspected ({ppm:F0} ppm)"
            : "Device clock skew suspected",
        SyncHealthVerdict.LocalTiming => "Local timing problem suspected",
        _ => "Sync issues detected (cause unclear)",
    };

    /// <inheritdoc/>
    public void Dispose() => _timer.Dispose();
}
