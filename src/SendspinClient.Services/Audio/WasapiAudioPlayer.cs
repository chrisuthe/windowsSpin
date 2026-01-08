// <copyright file="WasapiAudioPlayer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using SendspinClient.Services.Diagnostics;

namespace SendspinClient.Services.Audio;

/// <summary>
/// Specifies which resampler implementation to use for sync correction.
/// </summary>
public enum ResamplerType
{
    /// <summary>
    /// Use WDL (Cockos) resampler. Uses sinc interpolation.
    /// May cause artifacts during dynamic rate changes on some systems.
    /// </summary>
    Wdl,

    /// <summary>
    /// Use SoundTouch library. Uses WSOLA (time-stretch) algorithm.
    /// May produce smoother results for dynamic rate changes.
    /// </summary>
    SoundTouch,
}

/// <summary>
/// Windows WASAPI audio player using NAudio.
/// Provides low-latency audio output via WASAPI shared mode.
/// </summary>
/// <remarks>
/// <para>
/// Uses WASAPI shared mode for broad device compatibility. While exclusive mode
/// offers lower latency, shared mode is more reliable across different audio
/// hardware configurations and allows other applications to use audio simultaneously.
/// </para>
/// <para>
/// The 100ms latency setting provides stability across different hardware while
/// accounting for Windows Audio Engine overhead in shared mode. The actual latency
/// reported includes both the WASAPI buffer and additional Windows audio stack delays.
/// </para>
/// </remarks>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly SyncCorrectionStrategy _syncStrategy;
    private readonly ResamplerType _resamplerType;
    private readonly IDiagnosticAudioRecorder? _diagnosticRecorder;
    private string? _deviceId;
    private WasapiOut? _wasapiOut;
    private AudioSampleProviderAdapter? _sampleProvider;
    private ISampleProvider? _resamplerProvider; // Either WDL or SoundTouch
    private IDisposable? _resamplerDisposable; // For cleanup
    private ITimedAudioBuffer? _buffer;
    private ISyncCorrectionProvider? _correctionProvider;
    private SyncCorrectedSampleSource? _correctedSource;
    private AudioFormat? _format;
    private float _volume = 1.0f;
    private bool _isMuted;
    private int _outputLatencyMs;
    private int _deviceNativeSampleRate = 48000;

    /// <summary>
    /// Gets the detected output latency in milliseconds.
    /// This is the buffer latency reported by the WASAPI audio device.
    /// </summary>
    public int OutputLatencyMs => _outputLatencyMs;

    /// <summary>
    /// Gets the native sample rate of the audio output device.
    /// This is the rate the Windows audio mixer operates at.
    /// </summary>
    /// <remarks>
    /// Audio is resampled to this rate to avoid double-resampling by Windows Audio Engine.
    /// If the device rate cannot be queried, defaults to 48000 Hz.
    /// </remarks>
    public int DeviceNativeSampleRate => _deviceNativeSampleRate;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the format being sent to the audio device. When resampling is active,
    /// this reflects the device's native sample rate to avoid double-resampling by Windows Audio Engine.
    /// </remarks>
    public AudioFormat? OutputFormat =>
        _format == null ? null : new AudioFormat
        {
            Codec = _format.Codec,
            SampleRate = _syncStrategy == SyncCorrectionStrategy.Combined ? _deviceNativeSampleRate : _format.SampleRate,
            Channels = _format.Channels,
            BitDepth = _format.BitDepth,
            Bitrate = _format.Bitrate,
        };

    /// <summary>
    /// Gets the current sync correction mode from the external correction provider.
    /// </summary>
    /// <remarks>
    /// This exposes the correction mode from <see cref="SyncCorrectionCalculator"/> when using
    /// external sync correction (SDK 5.0+ architecture). Use this instead of
    /// <see cref="AudioBufferStats.CurrentCorrectionMode"/> which only reflects internal SDK correction.
    /// </remarks>
    public SyncCorrectionMode? ExternalCorrectionMode => _correctionProvider?.CurrentMode;

    /// <inheritdoc/>
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    /// <inheritdoc/>
    /// <remarks>
    /// Volume is applied in software via the sample provider by multiplying samples.
    /// This provides consistent behavior across different audio hardware.
    /// </remarks>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_sampleProvider != null)
            {
                _sampleProvider.Volume = _volume;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_sampleProvider != null)
            {
                _sampleProvider.IsMuted = value;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="WasapiAudioPlayer"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="deviceId">
    /// Optional device ID for a specific audio output device.
    /// If null or empty, the system default device is used.
    /// </param>
    /// <param name="syncStrategy">
    /// The sync correction strategy to use. Combined uses resampling for smooth correction,
    /// DropInsertOnly bypasses the resampler entirely for direct audio passthrough.
    /// </param>
    /// <param name="resamplerType">
    /// Which resampler implementation to use when strategy is Combined.
    /// WDL uses sinc interpolation, SoundTouch uses WSOLA algorithm.
    /// Ignored when strategy is DropInsertOnly.
    /// </param>
    /// <param name="diagnosticRecorder">Optional diagnostic recorder for audio capture.</param>
    public WasapiAudioPlayer(
        ILogger<WasapiAudioPlayer> logger,
        string? deviceId = null,
        SyncCorrectionStrategy syncStrategy = SyncCorrectionStrategy.Combined,
        ResamplerType resamplerType = ResamplerType.Wdl,
        IDiagnosticAudioRecorder? diagnosticRecorder = null)
    {
        _logger = logger;
        _deviceId = deviceId;
        _syncStrategy = syncStrategy;
        _resamplerType = resamplerType;
        _diagnosticRecorder = diagnosticRecorder;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    _format = format;

                    // Get the audio device - either specific device by ID or system default
                    MMDevice? device = null;
                    if (!string.IsNullOrEmpty(_deviceId))
                    {
                        try
                        {
                            using var enumerator = new MMDeviceEnumerator();
                            device = enumerator.GetDevice(_deviceId);
                            _logger.LogInformation("Using audio device: {DeviceName}", device.FriendlyName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get device {DeviceId}, falling back to default", _deviceId);
                            device = null;
                        }
                    }

                    // Query the device's native sample rate to avoid double-resampling
                    // WASAPI Shared mode resamples to the system mixer rate, so we'll
                    // resample once in our pipeline to match
                    _deviceNativeSampleRate = QueryDeviceMixFormat(device);

                    // Create WASAPI output in shared mode with 100ms latency
                    // Shared mode adds Windows Audio Engine overhead (~10-20ms) on top of
                    // the requested buffer latency, so we use 100ms for stability
                    const int RequestedLatencyMs = 100;

                    if (device != null)
                    {
                        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                    }
                    else
                    {
                        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                    }

                    _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                    // Query the actual output latency from the audio client
                    // This includes both the WASAPI buffer and Windows Audio Engine overhead
                    _outputLatencyMs = GetActualOutputLatency(_wasapiOut, RequestedLatencyMs);

                    SetState(AudioPlayerState.Stopped);
                    _logger.LogInformation(
                        "WASAPI player initialized: {SampleRate}Hz {Channels}ch, latency: {Latency}ms, device: {Device}",
                        format.SampleRate,
                        format.Channels,
                        _outputLatencyMs,
                        device?.FriendlyName ?? "System Default");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize WASAPI player");
                    SetState(AudioPlayerState.Error);
                    ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to initialize audio output", ex));
                    throw;
                }
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public void SetSampleSource(IAudioSampleSource source)
    {
        if (_wasapiOut == null || _format == null)
        {
            throw new InvalidOperationException("Player not initialized. Call InitializeAsync first.");
        }

        ArgumentNullException.ThrowIfNull(source);

        // Dispose previous resampler and correction source if any
        DisposeResampler();
        DisposeCorrectionSource();

        // Get buffer from source for sync correction (if source is BufferedAudioSampleSource)
        _buffer = null;
        _correctionProvider = null;
        IAudioSampleSource effectiveSource = source;

        if (source is BufferedAudioSampleSource bufferedSource)
        {
            _buffer = bufferedSource.Buffer;

            // Create correction options for drop/insert only (no resampling tier)
            // Set resampling threshold equal to deadband so it jumps straight to drop/insert
            var dropInsertOptions = _buffer.SyncOptions.Clone();
            dropInsertOptions.ResamplingThresholdMicroseconds = dropInsertOptions.EntryDeadbandMicroseconds;

            // Create correction provider for external sync correction
            var calculator = new SyncCorrectionCalculator(
                dropInsertOptions,
                _buffer.Format.SampleRate,
                _buffer.Format.Channels);
            _correctionProvider = calculator;

            // Create sync-corrected source that uses ReadRaw + external correction
            // This moves drop/insert logic out of the SDK into the app layer
            _correctedSource = new SyncCorrectedSampleSource(
                _buffer,
                _correctionProvider,
                () => HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds(),
                _logger);

            effectiveSource = _correctedSource;

            _logger.LogDebug(
                "Created SyncCorrectedSampleSource with external correction (SDK reports error, app applies correction)");
        }

        // Create NAudio adapter with current volume/mute state
        _sampleProvider = new AudioSampleProviderAdapter(effectiveSource, _format);
        _sampleProvider.Volume = _volume;
        _sampleProvider.IsMuted = _isMuted;

        // Optionally wrap with resampler for smooth sync correction
        // Pass device native sample rate for compound resampling (rate conversion + sync correction)
        if (_syncStrategy == SyncCorrectionStrategy.Combined && _correctionProvider != null)
        {
            CreateResampler(_sampleProvider);
            _wasapiOut.Init(_resamplerProvider);
            _logger.LogDebug(
                "Sample source configured with {ResamplerType} resampling: {SourceRate}Hz → {DeviceRate}Hz",
                _resamplerType,
                _format?.SampleRate,
                _deviceNativeSampleRate);
        }
        else
        {
            // DropInsertOnly: bypass resampler completely for direct audio passthrough
            _wasapiOut.Init(_sampleProvider);
            _logger.LogInformation(
                "Sample source configured with {Strategy} (no resampler in chain)",
                _syncStrategy);
        }
    }

    /// <summary>
    /// Disposes the current sync-corrected sample source.
    /// </summary>
    private void DisposeCorrectionSource()
    {
        _correctedSource?.Dispose();
        _correctedSource = null;
        _correctionProvider = null;
    }

    /// <summary>
    /// Creates the appropriate resampler based on configuration.
    /// </summary>
    private void CreateResampler(ISampleProvider sourceProvider)
    {
        if (_correctionProvider == null)
        {
            throw new InvalidOperationException("Correction provider must be set before creating resampler.");
        }

        switch (_resamplerType)
        {
            case ResamplerType.SoundTouch:
                // SoundTouch doesn't support sample rate conversion in the same pass,
                // so we don't pass target sample rate (it maintains the source rate)
                var soundTouch = new SoundTouchSampleProvider(
                    sourceProvider,
                    _correctionProvider,
                    _logger,
                    _diagnosticRecorder);
                _resamplerProvider = soundTouch;
                _resamplerDisposable = soundTouch;
                break;

            case ResamplerType.Wdl:
            default:
                var wdl = new DynamicResamplerSampleProvider(
                    sourceProvider,
                    _correctionProvider,
                    _deviceNativeSampleRate,
                    _logger,
                    _diagnosticRecorder);
                _resamplerProvider = wdl;
                _resamplerDisposable = wdl;
                break;
        }
    }

    /// <summary>
    /// Disposes the current resampler.
    /// </summary>
    private void DisposeResampler()
    {
        _resamplerDisposable?.Dispose();
        _resamplerProvider = null;
        _resamplerDisposable = null;
    }

    /// <inheritdoc/>
    public void Play()
    {
        if (_wasapiOut == null || _sampleProvider == null)
        {
            throw new InvalidOperationException("Player not initialized or no sample source set.");
        }

        _wasapiOut.Play();
        SetState(AudioPlayerState.Playing);
        _logger.LogInformation("Playback started");
    }

    /// <inheritdoc/>
    public void Pause()
    {
        _wasapiOut?.Pause();
        SetState(AudioPlayerState.Paused);
        _logger.LogInformation("Playback paused");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _wasapiOut?.Stop();
        SetState(AudioPlayerState.Stopped);
        _logger.LogInformation("Playback stopped");
    }

    /// <inheritdoc/>
    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    // Remember current state
                    var wasPlaying = State == AudioPlayerState.Playing;
                    var currentSampleProvider = _sampleProvider;

                    _logger.LogInformation(
                        "Switching audio device from {OldDevice} to {NewDevice}",
                        _deviceId ?? "System Default",
                        deviceId ?? "System Default");

                    // Stop and dispose current output
                    if (_wasapiOut != null)
                    {
                        _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                        _wasapiOut.Stop();
                        _wasapiOut.Dispose();
                        _wasapiOut = null;
                    }

                    // Update device ID
                    _deviceId = deviceId;

                    // Get the new audio device
                    MMDevice? device = null;
                    using var enumerator = new MMDeviceEnumerator();
                    if (!string.IsNullOrEmpty(_deviceId))
                    {
                        try
                        {
                            device = enumerator.GetDevice(_deviceId);
                            _logger.LogInformation("Using audio device: {DeviceName}", device.FriendlyName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get device {DeviceId}, falling back to default", _deviceId);
                            device = null;
                        }
                    }

                    // Query the new device's native sample rate
                    _deviceNativeSampleRate = QueryDeviceMixFormat(device);

                    // Create new WASAPI output with 100ms latency
                    const int RequestedLatencyMs = 100;
                    if (device != null)
                    {
                        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                    }
                    else
                    {
                        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                    }

                    _wasapiOut.PlaybackStopped += OnPlaybackStopped;
                    _outputLatencyMs = GetActualOutputLatency(_wasapiOut, RequestedLatencyMs);

                    // Reset sync tracking to prevent timing discontinuities from triggering false corrections
                    if (_buffer is TimedAudioBuffer timedBuffer)
                    {
                        timedBuffer.ResetSyncTracking();
                    }

                    _correctedSource?.Reset();

                    // Re-attach sample provider if we had one
                    // If we're using resampling, recreate the resampler with new device native rate
                    if (_resamplerProvider != null && currentSampleProvider != null)
                    {
                        // Recreate resampler with new device native rate
                        DisposeResampler();
                        CreateResampler(currentSampleProvider);

                        _wasapiOut.Init(_resamplerProvider);
                        _logger.LogDebug(
                            "Sample source re-attached to new device (with {ResamplerType} resampling at {DeviceRate}Hz)",
                            _resamplerType,
                            _deviceNativeSampleRate);
                    }
                    else if (currentSampleProvider != null)
                    {
                        _wasapiOut.Init(currentSampleProvider);
                        _logger.LogDebug("Sample source re-attached to new device");
                    }

                    SetState(AudioPlayerState.Stopped);

                    // Resume playback if we were playing
                    if (wasPlaying && currentSampleProvider != null)
                    {
                        _wasapiOut.Play();
                        SetState(AudioPlayerState.Playing);
                        _logger.LogInformation("Playback resumed on new device");
                    }

                    _logger.LogInformation(
                        "Audio device switched successfully: {Device}, latency: {Latency}ms",
                        device?.FriendlyName ?? "System Default",
                        _outputLatencyMs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to switch audio device");
                    SetState(AudioPlayerState.Error);
                    ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to switch audio device", ex));
                    throw;
                }
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_wasapiOut != null)
        {
            _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
            _wasapiOut.Stop();
            _wasapiOut.Dispose();
            _wasapiOut = null;
        }

        DisposeResampler();
        DisposeCorrectionSource();
        _sampleProvider = null;

        SetState(AudioPlayerState.Uninitialized);

        await Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Playback stopped due to error");
            SetState(AudioPlayerState.Error);
            ErrorOccurred?.Invoke(this, new AudioPlayerError("Playback error", e.Exception));
        }
        else if (State == AudioPlayerState.Playing)
        {
            // Unexpected stop while playing
            SetState(AudioPlayerState.Stopped);
        }
    }

    /// <summary>
    /// Queries the native sample rate of the audio device's mixer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In WASAPI shared mode, Windows Audio Engine resamples all audio to the device's
    /// native mixer rate. By querying this rate and resampling ourselves with high-quality
    /// filtering, we avoid double-resampling artifacts.
    /// </para>
    /// </remarks>
    /// <param name="device">The audio device to query, or null for system default.</param>
    /// <returns>The device's native sample rate in Hz, or 48000 if query fails.</returns>
    private int QueryDeviceMixFormat(MMDevice? device)
    {
        const int DefaultSampleRate = 48000;
        try
        {
            if (device == null)
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            var mixFormat = device.AudioClient.MixFormat;
            _logger.LogInformation(
                "Device native format: {SampleRate}Hz {Channels}ch {BitsPerSample}bit",
                mixFormat.SampleRate,
                mixFormat.Channels,
                mixFormat.BitsPerSample);
            return mixFormat.SampleRate;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query device mix format, defaulting to {DefaultRate}Hz", DefaultSampleRate);
            return DefaultSampleRate;
        }
    }

    /// <summary>
    /// Gets the actual output latency from the WASAPI audio client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NAudio's WasapiOut doesn't directly expose the StreamLatency property from the
    /// underlying AudioClient. We use reflection to access it when possible, falling
    /// back to the requested latency plus a safety margin for Windows Audio Engine overhead.
    /// </para>
    /// <para>
    /// In shared mode, Windows Audio Engine adds additional buffering (~10-20ms) on top
    /// of the requested latency. The StreamLatency property accounts for this overhead.
    /// </para>
    /// </remarks>
    /// <param name="wasapiOut">The WasapiOut instance to query.</param>
    /// <param name="requestedLatencyMs">The latency we requested when creating WasapiOut.</param>
    /// <returns>The actual output latency in milliseconds.</returns>
    private int GetActualOutputLatency(WasapiOut wasapiOut, int requestedLatencyMs)
    {
        try
        {
            // Try to get the actual stream latency via reflection
            // WasapiOut has a private 'audioClient' field of type AudioClient
            var audioClientField = typeof(WasapiOut).GetField(
                "audioClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (audioClientField?.GetValue(wasapiOut) is AudioClient audioClient)
            {
                // StreamLatency is in 100-nanosecond units, convert to milliseconds
                var streamLatency = audioClient.StreamLatency;
                var latencyMs = (int)(streamLatency / 10000);

                _logger.LogDebug(
                    "WASAPI StreamLatency: {StreamLatency} (100ns units) = {LatencyMs}ms",
                    streamLatency,
                    latencyMs);

                // Ensure we return at least the requested latency
                return Math.Max(latencyMs, requestedLatencyMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query WASAPI StreamLatency via reflection, using fallback");
        }

        // Fallback: use requested latency plus typical Windows Audio Engine overhead
        // In shared mode, Windows adds ~10-20ms of additional buffering
        const int WindowsAudioEngineOverheadMs = 15;
        var fallbackLatency = requestedLatencyMs + WindowsAudioEngineOverheadMs;

        _logger.LogDebug(
            "Using fallback output latency: {Latency}ms (requested: {Requested}ms + overhead: {Overhead}ms)",
            fallbackLatency,
            requestedLatencyMs,
            WindowsAudioEngineOverheadMs);

        return fallbackLatency;
    }

    private void SetState(AudioPlayerState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Player state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
}
