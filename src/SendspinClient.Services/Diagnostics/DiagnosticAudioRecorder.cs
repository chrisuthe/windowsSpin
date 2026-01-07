// <copyright file="DiagnosticAudioRecorder.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Diagnostics;
using Sendspin.SDK.Synchronization;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Implements diagnostic audio recording with sync metric correlation.
/// </summary>
/// <remarks>
/// <para>
/// This class orchestrates audio capture and metric recording for diagnostic purposes.
/// When enabled, it maintains circular buffers for both audio samples and sync metrics,
/// allowing the user to save a snapshot of the last N seconds of audio with embedded
/// markers showing sync state at each moment.
/// </para>
/// <para>
/// Memory usage: ~17MB for 45 seconds of 48kHz stereo audio, plus ~40KB for metrics.
/// Memory is only allocated when enabled.
/// </para>
/// </remarks>
public sealed class DiagnosticAudioRecorder : IDiagnosticAudioRecorder
{
    private readonly ILogger<DiagnosticAudioRecorder> _logger;
    private readonly object _lock = new();

    private DiagnosticAudioRingBuffer? _audioBuffer;
    private SyncMetricRingBuffer? _metricBuffer;
    private bool _isEnabled;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _isEnabled;
            }
        }
    }

    /// <inheritdoc/>
    public double BufferedSeconds
    {
        get
        {
            lock (_lock)
            {
                return _audioBuffer?.BufferedSeconds ?? 0;
            }
        }
    }

    /// <inheritdoc/>
    public int BufferDurationSeconds { get; }

    /// <inheritdoc/>
    public event EventHandler<bool>? RecordingStateChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticAudioRecorder"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="bufferDurationSeconds">The buffer duration in seconds (default: 45).</param>
    public DiagnosticAudioRecorder(ILogger<DiagnosticAudioRecorder> logger, int bufferDurationSeconds = 45)
    {
        _logger = logger;
        BufferDurationSeconds = bufferDurationSeconds;
    }

    /// <inheritdoc/>
    public void Enable(int sampleRate, int channels)
    {
        lock (_lock)
        {
            if (_isEnabled)
            {
                _logger.LogDebug("Diagnostic recording already enabled");
                return;
            }

            _audioBuffer = new DiagnosticAudioRingBuffer(sampleRate, channels, BufferDurationSeconds);
            _metricBuffer = new SyncMetricRingBuffer();
            _isEnabled = true;

            _logger.LogInformation(
                "Diagnostic recording enabled: {SampleRate}Hz, {Channels}ch, {Duration}s buffer (~{MemoryMB:F1}MB)",
                sampleRate,
                channels,
                BufferDurationSeconds,
                _audioBuffer.Capacity * sizeof(float) / 1024.0 / 1024.0);
        }

        RecordingStateChanged?.Invoke(this, true);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        lock (_lock)
        {
            if (!_isEnabled)
            {
                return;
            }

            _audioBuffer = null;
            _metricBuffer = null;
            _isEnabled = false;

            _logger.LogInformation("Diagnostic recording disabled, buffers freed");
        }

        RecordingStateChanged?.Invoke(this, false);
    }

    /// <inheritdoc/>
    public void CaptureIfEnabled(ReadOnlySpan<float> samples)
    {
        // Fast path when disabled - single volatile read
        if (!_isEnabled)
        {
            return;
        }

        // Capture samples (lock-free write to ring buffer)
        _audioBuffer?.Write(samples);
    }

    /// <inheritdoc/>
    public void RecordMetrics(AudioBufferStats stats)
    {
        if (!_isEnabled)
        {
            return;
        }

        var audioBuffer = _audioBuffer;
        var metricBuffer = _metricBuffer;

        if (audioBuffer == null || metricBuffer == null)
        {
            return;
        }

        var snapshot = new SyncMetricSnapshot
        {
            TimestampMicroseconds = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds(),
            SamplePosition = audioBuffer.TotalSamplesWritten,
            RawSyncErrorMicroseconds = stats.SyncErrorMicroseconds,
            SmoothedSyncErrorMicroseconds = stats.SyncErrorMicroseconds, // Same value (smoothed is what's exposed)
            CorrectionMode = stats.CurrentCorrectionMode,
            PlaybackRate = stats.TargetPlaybackRate,
            BufferDepthMs = stats.BufferedMs,
        };

        metricBuffer.Record(snapshot);
    }

    /// <inheritdoc/>
    public async Task<string?> SaveAsync(string directory)
    {
        DiagnosticAudioRingBuffer? audioBuffer;
        SyncMetricRingBuffer? metricBuffer;

        lock (_lock)
        {
            if (!_isEnabled || _audioBuffer == null || _metricBuffer == null)
            {
                _logger.LogWarning("Cannot save: diagnostic recording is not enabled");
                return null;
            }

            audioBuffer = _audioBuffer;
            metricBuffer = _metricBuffer;
        }

        // Capture snapshots (this allocates, but we're on a background thread)
        var (samples, startIndex) = audioBuffer.CaptureSnapshot();
        var endIndex = startIndex + samples.Length;
        var metrics = metricBuffer.GetSnapshotsInRange(startIndex, endIndex);

        if (samples.Length == 0)
        {
            _logger.LogWarning("Cannot save: no audio samples in buffer");
            return null;
        }

        // Ensure directory exists
        Directory.CreateDirectory(directory);

        // Generate filename with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var baseName = $"diagnostic-{timestamp}";
        var wavPath = Path.Combine(directory, $"{baseName}.wav");
        var labelPath = Path.Combine(directory, $"{baseName}.txt");

        _logger.LogInformation(
            "Saving diagnostic recording: {Samples} samples ({Duration:F1}s), {Metrics} markers",
            samples.Length,
            (double)samples.Length / audioBuffer.SampleRate / audioBuffer.Channels,
            metrics.Length);

        // Write files on background thread
        await Task.Run(() =>
        {
            // Write WAV file with cue markers
            DiagnosticWavWriter.WriteWavWithMarkers(
                wavPath,
                samples,
                audioBuffer.SampleRate,
                audioBuffer.Channels,
                metrics,
                startIndex);

            // Write Audacity label file
            WriteAudacityLabels(labelPath, metrics, audioBuffer.SampleRate, audioBuffer.Channels, startIndex);
        });

        _logger.LogInformation("Diagnostic recording saved: {WavPath}", wavPath);

        return wavPath;
    }

    /// <summary>
    /// Writes an Audacity-compatible label file.
    /// </summary>
    private static void WriteAudacityLabels(
        string path,
        SyncMetricSnapshot[] metrics,
        int sampleRate,
        int channels,
        long startSamplePosition)
    {
        using var writer = new StreamWriter(path);

        foreach (var metric in metrics)
        {
            // Calculate time position relative to the start of the WAV
            var sampleOffset = metric.SamplePosition - startSamplePosition;
            var timeSeconds = (double)sampleOffset / sampleRate / channels;

            if (timeSeconds >= 0)
            {
                writer.WriteLine(metric.FormatAudacityLabel(timeSeconds));
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disable();
    }
}
