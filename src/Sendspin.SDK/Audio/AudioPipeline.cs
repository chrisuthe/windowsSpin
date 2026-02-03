// <copyright file="AudioPipeline.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Orchestrates the complete audio pipeline from incoming chunks to output.
/// Manages decoder, buffer, and player lifecycle and coordinates their interaction.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline operates in the following states:
/// - Idle: No active stream
/// - Starting: Initializing components for a new stream
/// - Buffering: Accumulating audio before playback starts
/// - Playing: Actively playing audio
/// - Stopping: Shutting down the current stream
/// - Error: An error occurred
/// </para>
/// <para>
/// Audio flow:
/// 1. ProcessAudioChunk receives encoded audio with server timestamp
/// 2. Decoder converts to float PCM samples
/// 3. TimedAudioBuffer stores samples with playback timestamps
/// 4. NAudio reads from buffer when samples are due for playback
/// </para>
/// </remarks>
public sealed class AudioPipeline : IAudioPipeline
{
    private readonly ILogger<AudioPipeline> _logger;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IClockSynchronizer _clockSync;
    private readonly Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> _bufferFactory;
    private readonly Func<IAudioPlayer> _playerFactory;
    private readonly Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> _sourceFactory;
    private readonly IHighPrecisionTimer _precisionTimer;

    private IAudioDecoder? _decoder;
    private ITimedAudioBuffer? _buffer;
    private IAudioPlayer? _player;
    private IAudioSampleSource? _sampleSource;

    private float[] _decodeBuffer = Array.Empty<float>();
    private AudioFormat? _currentFormat;
    private int _volume = 100;
    private bool _muted;
    private long _lastSyncLogTime;

    // How often to log sync status during playback (microseconds)
    private const long SyncLogIntervalMicroseconds = 5_000_000; // 5 seconds

    // Clock sync wait configuration
    private readonly bool _waitForConvergence;
    private readonly int _convergenceTimeoutMs;
    private long _bufferReadyTime;
    private bool _loggedSyncWaiting;

    /// <inheritdoc/>
    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;

    /// <inheritdoc/>
    public bool IsReady => _decoder != null && _buffer != null;

    /// <inheritdoc/>
    public AudioBufferStats? BufferStats => _buffer?.GetStats();

    /// <inheritdoc/>
    public AudioFormat? CurrentFormat => _currentFormat;

    /// <inheritdoc/>
    public AudioFormat? OutputFormat => _player?.OutputFormat;

    /// <inheritdoc/>
    public int DetectedOutputLatencyMs => _player?.OutputLatencyMs ?? 0;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="decoderFactory">Factory for creating audio decoders.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferFactory">Factory for creating timed audio buffers.</param>
    /// <param name="playerFactory">Factory for creating audio players.</param>
    /// <param name="sourceFactory">Factory for creating sample sources.</param>
    /// <param name="precisionTimer">High-precision timer for accurate timing (optional, uses shared instance if null).</param>
    /// <param name="waitForConvergence">Whether to wait for clock sync convergence before starting playback (default: true).</param>
    /// <param name="convergenceTimeoutMs">Timeout in milliseconds to wait for clock sync convergence (default: 5000ms).</param>
    /// <param name="useMonotonicTimer">Whether to wrap the timer with monotonicity enforcement for VM resilience (default: true).</param>
    public AudioPipeline(
        ILogger<AudioPipeline> logger,
        IAudioDecoderFactory decoderFactory,
        IClockSynchronizer clockSync,
        Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> bufferFactory,
        Func<IAudioPlayer> playerFactory,
        Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> sourceFactory,
        IHighPrecisionTimer? precisionTimer = null,
        bool waitForConvergence = true,
        int convergenceTimeoutMs = 5000,
        bool useMonotonicTimer = true)
    {
        _logger = logger;
        _decoderFactory = decoderFactory;
        _clockSync = clockSync;
        _bufferFactory = bufferFactory;
        _playerFactory = playerFactory;
        _sourceFactory = sourceFactory;
        _waitForConvergence = waitForConvergence;
        _convergenceTimeoutMs = convergenceTimeoutMs;

        // Set up the precision timer, optionally wrapping with monotonic filter for VM resilience
        var baseTimer = precisionTimer ?? HighPrecisionTimer.Shared;
        if (useMonotonicTimer)
        {
            _precisionTimer = new MonotonicTimer(baseTimer, logger);
            _logger.LogDebug("Using MonotonicTimer wrapper for VM-resilient timing");
        }
        else
        {
            _precisionTimer = baseTimer;
        }

        // Log timer precision at startup
        if (HighPrecisionTimer.IsHighResolution)
        {
            _logger.LogDebug(
                "Using high-precision timer with {Resolution:F2}ns resolution",
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        else
        {
            _logger.LogWarning("High-resolution timing not available, sync accuracy may be reduced");
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        // Stop any existing stream first
        if (State != AudioPipelineState.Idle && State != AudioPipelineState.Error)
        {
            await StopAsync();
        }

        SetState(AudioPipelineState.Starting);

        try
        {
            _currentFormat = format;

            // Create decoder for the stream format
            _decoder = _decoderFactory.Create(format);
            _decodeBuffer = new float[_decoder.MaxSamplesPerFrame];

            _logger.LogDebug(
                "Decoder created for {Codec}, max frame size: {MaxSamples} samples",
                format.Codec,
                _decoder.MaxSamplesPerFrame);

            // Create timed buffer
            _buffer = _bufferFactory(format, _clockSync);

            // Subscribe to buffer reanchor event (if buffer supports it)
            if (_buffer is TimedAudioBuffer timedBuffer)
            {
                timedBuffer.ReanchorRequired += OnReanchorRequired;
            }

            // Create audio player
            _player = _playerFactory();
            await _player.InitializeAsync(format, cancellationToken);

            // Set output latency for diagnostic/logging purposes
            _buffer.OutputLatencyMicroseconds = _player.OutputLatencyMs * 1000L;

            // Set calibrated startup latency for sync error compensation (push-model backends only)
            _buffer.CalibratedStartupLatencyMicroseconds = _player.CalibratedStartupLatencyMs * 1000L;
            _logger.LogDebug(
                "Output latency: {LatencyMs}ms, Calibrated startup latency: {CalibratedMs}ms",
                _player.OutputLatencyMs,
                _player.CalibratedStartupLatencyMs);

            // Create sample source bridging buffer to player
            _sampleSource = _sourceFactory(_buffer, GetCurrentLocalTimeMicroseconds);
            _player.SetSampleSource(_sampleSource);

            // Apply volume/mute settings
            _player.Volume = _volume / 100f;
            _player.IsMuted = _muted;

            // Subscribe to player events
            _player.StateChanged += OnPlayerStateChanged;
            _player.ErrorOccurred += OnPlayerError;

            SetState(AudioPipelineState.Buffering);
            _logger.LogInformation("Audio pipeline started: {Format}", format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio pipeline");
            await CleanupAsync();
            SetState(AudioPipelineState.Error);
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start pipeline", ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (State == AudioPipelineState.Idle)
        {
            return;
        }

        SetState(AudioPipelineState.Stopping);

        await CleanupAsync();

        SetState(AudioPipelineState.Idle);
        _logger.LogInformation("Audio pipeline stopped");
    }

    /// <inheritdoc/>
    public void Clear(long? newTargetTimestamp = null)
    {
        _buffer?.Clear();
        _decoder?.Reset();

        // Reset sync wait state so we wait for convergence again after clear
        _bufferReadyTime = 0;
        _loggedSyncWaiting = false;

        if (State == AudioPipelineState.Playing)
        {
            SetState(AudioPipelineState.Buffering);
        }

        _logger.LogDebug("Audio buffer cleared");
    }

    /// <inheritdoc/>
    public void ProcessAudioChunk(AudioChunk chunk)
    {
        if (_decoder == null || _buffer == null)
        {
            _logger.LogWarning("Received audio chunk but pipeline not started");
            return;
        }

        try
        {
            // Decode the audio frame
            var samplesDecoded = _decoder.Decode(chunk.EncodedData, _decodeBuffer);

            if (samplesDecoded > 0)
            {
                // Add decoded samples to buffer with server timestamp
                _buffer.Write(_decodeBuffer.AsSpan(0, samplesDecoded), chunk.ServerTimestamp);

                // Periodically log sync error during playback
                if (State == AudioPipelineState.Playing)
                {
                    LogSyncStatusIfNeeded();
                }

                // Start playback when buffer is ready AND (optionally) clock is synced
                // JS client approach: wait for clock sync convergence to ensure accurate timing
                if (State == AudioPipelineState.Buffering && _buffer.IsReadyForPlayback)
                {
                    if (ShouldWaitForClockSync())
                    {
                        LogSyncWaitingIfNeeded();
                    }
                    else
                    {
                        StartPlayback();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash - one bad frame shouldn't stop the stream
            _logger.LogWarning(ex, "Error processing audio chunk, skipping frame");
        }
    }

    /// <inheritdoc/>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_player != null)
        {
            _player.Volume = _volume / 100f;
        }

        _logger.LogDebug("Volume set to {Volume}%", _volume);
    }

    /// <inheritdoc/>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_player != null)
        {
            _player.IsMuted = muted;
        }

        _logger.LogDebug("Mute set to {Muted}", muted);
    }

    /// <inheritdoc/>
    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (_player == null)
        {
            _logger.LogWarning("Cannot switch audio device - pipeline not started");
            return;
        }

        var wasPlaying = State == AudioPipelineState.Playing;

        _logger.LogInformation("Switching audio device, currently {State}", State);

        try
        {
            // Switch the audio device - this stops/restarts playback internally
            await _player.SwitchDeviceAsync(deviceId, cancellationToken);

            // Update the buffer's latency values for the new device
            // The new device may have different latency characteristics
            if (_buffer != null)
            {
                _buffer.OutputLatencyMicroseconds = _player.OutputLatencyMs * 1000L;
                _buffer.CalibratedStartupLatencyMicroseconds = _player.CalibratedStartupLatencyMs * 1000L;
                _logger.LogDebug(
                    "Updated latencies after device switch: output={LatencyMs}ms, calibrated={CalibratedMs}ms",
                    _player.OutputLatencyMs,
                    _player.CalibratedStartupLatencyMs);

                // Trigger a soft re-anchor to reset sync error tracking
                // This prevents the timing discontinuity from causing false sync corrections
                if (_buffer is TimedAudioBuffer timedBuffer)
                {
                    timedBuffer.ResetSyncTracking();
                    _logger.LogDebug("Reset sync tracking after device switch");
                }
            }

            // If we were playing and the player resumed, ensure state is correct
            if (wasPlaying && _player.State == AudioPlayerState.Playing)
            {
                // Reset sync monitoring counters since timing has been reset
                _lastSyncLogTime = _precisionTimer.GetCurrentTimeMicroseconds();

                SetState(AudioPipelineState.Playing);
            }
            else if (wasPlaying)
            {
                // Player didn't resume automatically - might need buffering
                SetState(AudioPipelineState.Buffering);
            }

            _logger.LogInformation(
                "Audio device switched successfully, output latency: {LatencyMs}ms",
                _player.OutputLatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch audio device");
            SetState(AudioPipelineState.Error);
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to switch audio device", ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Gets the current local time in microseconds using high-precision timer.
    /// Used by the sample source to know when to release audio.
    /// </summary>
    /// <remarks>
    /// Uses Stopwatch-based timing instead of DateTimeOffset for microsecond precision.
    /// DateTimeOffset.UtcNow only has ~15ms resolution on Windows.
    /// </remarks>
    private long GetCurrentLocalTimeMicroseconds()
    {
        return _precisionTimer.GetCurrentTimeMicroseconds();
    }

    private void StartPlayback()
    {
        if (_player == null)
        {
            return;
        }

        try
        {
            var syncStatus = _clockSync.GetStatus();
            _player.Play();

            // Reset sync monitoring counter
            _lastSyncLogTime = _precisionTimer.GetCurrentTimeMicroseconds();

            SetState(AudioPipelineState.Playing);
            _logger.LogInformation(
                "Starting playback: buffer={BufferMs:F0}ms, sync offset={OffsetMs:F2}ms (±{UncertaintyMs:F2}ms), " +
                "output latency={OutputLatencyMs}ms, timer resolution={ResolutionNs:F0}ns",
                _buffer?.BufferedMilliseconds ?? 0,
                syncStatus.OffsetMilliseconds,
                syncStatus.OffsetUncertaintyMicroseconds / 1000.0,
                DetectedOutputLatencyMs,
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start playback");
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start playback", ex));
        }
    }

    /// <summary>
    /// Determines whether we should wait for clock sync convergence before starting playback.
    /// </summary>
    /// <returns>True if we should wait, false if we can proceed with playback.</returns>
    private bool ShouldWaitForClockSync()
    {
        // If wait is disabled, always proceed
        if (!_waitForConvergence)
        {
            return false;
        }

        // If clock has minimal sync (2+ measurements), proceed
        // Full convergence happens in background, sync correction handles any estimation errors
        if (_clockSync.HasMinimalSync)
        {
            return false;
        }

        // Track when buffer first became ready (for timeout calculation)
        if (_bufferReadyTime == 0)
        {
            _bufferReadyTime = _precisionTimer.GetCurrentTimeMicroseconds();
        }

        // Check for timeout - proceed anyway if we've waited too long
        var elapsed = _precisionTimer.GetCurrentTimeMicroseconds() - _bufferReadyTime;
        if (elapsed > _convergenceTimeoutMs * 1000L)
        {
            var status = _clockSync.GetStatus();
            _logger.LogWarning(
                "Clock sync timeout after {ElapsedMs}ms. Starting playback without full convergence. " +
                "Measurements: {Count}, Uncertainty: {Uncertainty:F2}ms",
                elapsed / 1000,
                status.MeasurementCount,
                status.OffsetUncertaintyMicroseconds / 1000.0);
            return false; // Timeout - proceed anyway
        }

        return true; // Still waiting for convergence
    }

    /// <summary>
    /// Logs that we're waiting for clock sync convergence (only once per wait period).
    /// </summary>
    private void LogSyncWaitingIfNeeded()
    {
        if (!_loggedSyncWaiting)
        {
            _loggedSyncWaiting = true;
            var status = _clockSync.GetStatus();
            _logger.LogInformation(
                "Buffer ready ({BufferMs:F0}ms), waiting for clock sync convergence. " +
                "Measurements: {Count}, Uncertainty: {Uncertainty:F2}ms, Converged: {Converged}",
                _buffer?.BufferedMilliseconds ?? 0,
                status.MeasurementCount,
                status.OffsetUncertaintyMicroseconds / 1000.0,
                status.IsConverged);
        }
    }

    private async Task CleanupAsync()
    {
        // Unsubscribe from events
        if (_player != null)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.ErrorOccurred -= OnPlayerError;
            _player.Stop();
            await _player.DisposeAsync();
            _player = null;
        }

        // Unsubscribe from buffer events
        if (_buffer is TimedAudioBuffer timedBuffer)
        {
            timedBuffer.ReanchorRequired -= OnReanchorRequired;
        }

        _decoder?.Dispose();
        _decoder = null;

        _buffer?.Dispose();
        _buffer = null;

        _sampleSource = null;
        _decodeBuffer = Array.Empty<float>();
        _currentFormat = null;
    }

    private void OnReanchorRequired(object? sender, EventArgs e)
    {
        var stats = _buffer?.GetStats();
        _logger.LogWarning(
            "Re-anchoring required: sync error {SyncErrorMs:F1}ms exceeds threshold. " +
            "Dropped={Dropped}, Inserted={Inserted}. Clearing buffer for resync.",
            stats?.SyncErrorMs ?? 0,
            stats?.SamplesDroppedForSync ?? 0,
            stats?.SamplesInsertedForSync ?? 0);

        // Clear and restart buffering
        Clear();
    }

    private void OnPlayerStateChanged(object? sender, AudioPlayerState state)
    {
        if (state == AudioPlayerState.Error)
        {
            SetState(AudioPipelineState.Error);
        }
    }

    private void OnPlayerError(object? sender, AudioPlayerError error)
    {
        _logger.LogError(error.Exception, "Player error: {Message}", error.Message);
        ErrorOccurred?.Invoke(this, new AudioPipelineError(error.Message, error.Exception));
    }

    private void SetState(AudioPipelineState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Pipeline state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    /// <summary>
    /// Logs sync status periodically during playback for monitoring drift.
    /// </summary>
    private void LogSyncStatusIfNeeded()
    {
        var currentTime = _precisionTimer.GetCurrentTimeMicroseconds();

        // Only log every SyncLogIntervalMicroseconds
        if (currentTime - _lastSyncLogTime < SyncLogIntervalMicroseconds)
        {
            return;
        }

        _lastSyncLogTime = currentTime;
        var stats = _buffer?.GetStats();
        var clockStatus = _clockSync.GetStatus();

        if (stats is { IsPlaybackActive: true })
        {
            var syncErrorMs = stats.SyncErrorMs;
            var absError = Math.Abs(syncErrorMs);

            // Format correction mode for logging
            var correctionInfo = stats.CurrentCorrectionMode switch
            {
                SyncCorrectionMode.Dropping => $"DROPPING (dropped={stats.SamplesDroppedForSync})",
                SyncCorrectionMode.Inserting => $"INSERTING (inserted={stats.SamplesInsertedForSync})",
                _ => "none",
            };

            // Calculate derived values for debugging
            var samplesReadTimeMs = stats.SamplesReadSinceStart * (1000.0 / (_currentFormat!.SampleRate * _currentFormat.Channels));

            // Get drift status for enhanced diagnostics
            var driftInfo = clockStatus.IsDriftReliable
                ? $"drift={clockStatus.DriftMicrosecondsPerSecond:+0.0;-0.0}μs/s"
                : "drift=pending";

            // Use appropriate log level based on sync error magnitude
            if (absError > 50) // > 50ms - significant drift
            {
                _logger.LogWarning(
                    "Sync drift: error={SyncErrorMs:+0.00;-0.00}ms, elapsed={Elapsed:F0}ms, readTime={ReadTime:F0}ms, " +
                    "latencyComp={Latency}ms, {DriftInfo}, correction={Correction}, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    stats.ElapsedSinceStartMs,
                    samplesReadTimeMs,
                    _buffer?.OutputLatencyMicroseconds / 1000 ?? 0,
                    driftInfo,
                    correctionInfo,
                    stats.BufferedMs);
            }
            else if (absError > 10) // > 10ms - noticeable
            {
                _logger.LogInformation(
                    "Sync status: error={SyncErrorMs:+0.00;-0.00}ms, elapsed={Elapsed:F0}ms, readTime={ReadTime:F0}ms, " +
                    "{DriftInfo}, correction={Correction}, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    stats.ElapsedSinceStartMs,
                    samplesReadTimeMs,
                    driftInfo,
                    correctionInfo,
                    stats.BufferedMs);
            }
            else // < 10ms - good sync
            {
                _logger.LogDebug(
                    "Sync OK: error={SyncErrorMs:+0.00;-0.00}ms, {DriftInfo}, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    driftInfo,
                    stats.BufferedMs);
            }
        }
    }
}
