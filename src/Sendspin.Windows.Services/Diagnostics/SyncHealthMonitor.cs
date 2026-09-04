// <copyright file="SyncHealthMonitor.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio;

namespace Sendspin.Windows.Services.Diagnostics;

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
    // SDK 9.3.0: dead band 100 µs, speed cap 0.5% (the spec's MUST ceiling).
    private const double DeadbandMs = 0.1;
    private const double MaxSpeedCorrection = 0.005;

    private readonly IAudioPipeline _pipeline;
    private readonly IClockSynchronizer _clockSync;
    private readonly ReadCallbackGapTracker _gapTracker;
    private readonly SyncHealthLog _log;
    private readonly OutputLatencyReporter _latencyReporter;
    private readonly ILogger<SyncHealthMonitor> _logger;
    private readonly EpisodeDetector _detector = new(DeadbandMs, MaxSpeedCorrection);
    private readonly Timer _timer;

    private volatile bool _wasActive;
    private volatile bool _tickFaulted;
    private int _tickRunning;
    private int _episodeCount;
    private volatile string _healthDisplay = "No issues detected";

    /// <summary>Gets the latest verdict line for the Stats window (e.g. "Network starvation suspected (2 episodes)").</summary>
    public string HealthDisplay => _healthDisplay;

    /// <summary>Gets the number of episodes recorded this session.</summary>
    public int EpisodeCount => Volatile.Read(ref _episodeCount);

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
        OutputLatencyReporter latencyReporter,
        ILogger<SyncHealthMonitor> logger)
    {
        _pipeline = pipeline;
        _clockSync = clockSync;
        _gapTracker = gapTracker;
        _log = log;
        _latencyReporter = latencyReporter;
        _logger = logger;
        _timer = new Timer(OnTick, state: null, dueTime: SampleIntervalMs, period: SampleIntervalMs);
    }

    private void OnTick(object? state)
    {
        // Timer callbacks can overlap if a tick stalls >100ms (e.g. slow log I/O);
        // the detector is single-threaded by contract, so skip overlapping ticks.
        if (Interlocked.Exchange(ref _tickRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var stats = _pipeline.BufferStats;
            if (stats is null)
            {
                _wasActive = false;
                return;
            }

            // Defer the header until the output format is known so the log line is useful.
            if (!_wasActive && _pipeline.OutputFormat is not null)
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
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    /// <summary>
    /// Writes the one-line session header, including how the output latency was arrived at.
    /// </summary>
    /// <remarks>
    /// The latency comes from the reporter rather than the pipeline. The header is written on the
    /// first tick that sees an output format, which can beat the player's latency ladder by a few
    /// hundred milliseconds - so the pipeline's figure at that instant may still be the pre-Init
    /// placeholder. A session that recorded "outputLatency=115ms" while the ladder resolved 100ms
    /// four tenths of a second later is a real observation, and it made the header untrustworthy
    /// for the one field it exists to report. Naming the provenance means a placeholder now says
    /// so, and the ladder's own log line carries the resolved value.
    /// </remarks>
    private void WriteSessionHeader()
    {
        var format = _pipeline.OutputFormat;
        var version = typeof(SyncHealthMonitor).Assembly.GetName().Version?.ToString(3) ?? "?";
        var latency = FormatOutputLatency(_latencyReporter.Current, _pipeline.DetectedOutputLatencyMs);

        _log.WriteSessionHeader(
            $"app={version} os={Environment.OSVersion.VersionString} " +
            $"format={format?.SampleRate}Hz/{format?.Channels}ch " +
            $"outputLatency={latency}");
    }

    /// <summary>
    /// Renders the session header's output latency, naming where the figure came from.
    /// </summary>
    /// <param name="reading">The player's published reading, or null if it has not resolved one.</param>
    /// <param name="pipelineLatencyMs">The pipeline's figure, used only when there is no reading.</param>
    /// <returns>The formatted latency field.</returns>
    public static string FormatOutputLatency(OutputLatencyReading? reading, int pipelineLatencyMs) =>
        reading is null
            ? $"{pipelineLatencyMs}ms (unreported)"
            : $"{reading.LatencyMs}ms ({reading.Provenance})";

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
