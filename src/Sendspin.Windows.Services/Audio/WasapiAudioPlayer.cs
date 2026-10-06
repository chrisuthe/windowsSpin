// <copyright file="WasapiAudioPlayer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Models;

namespace Sendspin.Windows.Services.Audio;

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
    private readonly SyncCorrectionMechanism _mechanism;
    private string? _deviceId;
    private WasapiOut? _wasapiOut;
    private AudioSampleProviderAdapter? _sampleProvider;
    private ITimedAudioBuffer? _buffer;
    private SyncCorrectedSampleSource? _correctedSource;
    private AudioFormat? _format;
    private float _volume = 1.0f;
    private bool _isMuted;
    private OutputLatencyReading? _outputLatency;
    private int _deviceNativeSampleRate = 48000;

    // Optional WASAPI device clock as the sync-timing source (issue #33). OFF by default: the device
    // clock reads the DAC-rendered position, which permanently lags the samples read from our buffer
    // by the ~100ms WASAPI prefill, producing a constant -100ms sync error that pushes the player off
    // the shared schedule (out of sync with other players). The wall-clock default tracks real
    // playback 1:1 and holds sync, as it did in 2.1.0. Opt in only for genuinely divergent DAC clocks.
    private readonly bool _useDeviceClock;
    private readonly DeviceClockAnchor _deviceClockAnchor = new();
    private AudioClockClient? _audioClockClient;
    private long _lastClockProbeLogTicks;
    private long _probeLastClockMicros;
    private long _probeLastWallMicros;
    private const long ClockProbeLogIntervalTicks = TimeSpan.TicksPerSecond * 5;

    // Device-invalidation recovery. AUDCLNT_E_DEVICE_INVALIDATED (0x88890004) fires when the output
    // device is pulled out from under WASAPI - default device changed, device disabled/unplugged/
    // reformatted, or grabbed in exclusive mode. Instead of dying in a terminal Error state (silence
    // until restart), re-initialize the output on the current device and resume.
    private int _recovering;
    private const int DeviceInvalidatedHResult = unchecked((int)0x88890004);
    private const int MaxDeviceRecoveryAttempts = 5;
    private const int DeviceRecoveryBaseDelayMs = 200;

    // Latency requested when creating WasapiOut, and the Windows Audio Engine overhead used only as a
    // fallback. The real device latency is read from IAudioClient.StreamLatency AFTER Init() (querying
    // before Init throws AUDCLNT_E_NOT_INITIALIZED).
    private const int RequestedLatencyMs = 100;
    private const int WindowsAudioEngineOverheadMs = 15;

    private readonly OutputLatencyReporter? _latencyReporter;

    /// <summary>
    /// Gets the detected output latency in milliseconds.
    /// This is the buffer latency reported by the WASAPI audio device.
    /// </summary>
    /// <remarks>
    /// Read from the same reading the reporter publishes, under <see cref="Volatile"/>. The SDK
    /// subtracts this from the sync error on a different thread to the one that resolves it, so a
    /// plain field leaves no guarantee the pipeline ever sees a device switch's new value.
    /// </remarks>
    public int OutputLatencyMs => Volatile.Read(ref _outputLatency)?.LatencyMs ?? 0;

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
            SampleRate = _mechanism == SyncCorrectionMechanism.SmoothResampling ? _deviceNativeSampleRate : _format.SampleRate,
            Channels = _format.Channels,
            BitDepth = _format.BitDepth,
            Bitrate = _format.Bitrate,
        };

    /// <summary>
    /// Gets the current sync correction mode from the SDK's external correction provider.
    /// </summary>
    /// <remarks>
    /// This exposes the correction mode from <see cref="SyncCorrectionCalculator"/>, which drives
    /// the external correction path. Use this instead of
    /// <see cref="AudioBufferStats.CurrentCorrectionMode"/> which only reflects internal SDK correction.
    /// </remarks>
    public SyncCorrectionMode? ExternalCorrectionMode => _correctedSource?.CorrectionProvider.CurrentMode;

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
    /// <param name="mechanism">
    /// How the SDK's correction source realizes the continuous correction tier, and with it whether
    /// this player carries a resampler at all. <see cref="SyncCorrectionMechanism.SmoothResampling"/>
    /// trims playback speed and also converts to the device's native rate;
    /// <see cref="SyncCorrectionMechanism.FrameStepping"/> steps whole frames and keeps every
    /// resampler out of the output chain, leaving any rate conversion to the Windows Audio Engine.
    /// Must match the <see cref="SyncCorrectionOptions.Mechanism"/> the pipeline's buffers carry.
    /// </param>
    /// <param name="useDeviceClock">
    /// When false (default), sync is timed against the wall clock (<c>HighPrecisionTimer</c>), which
    /// tracks real playback 1:1 and holds multi-room sync — the 2.1.0 behavior. When true, sync is
    /// timed against the WASAPI device clock (IAudioClock); this reads the DAC-rendered position,
    /// which lags the buffer read pointer by the ~100ms output prefill and leaves the player ~100ms
    /// off the shared schedule, so it is opt-in only for genuinely divergent DAC clocks. Falls back
    /// to the wall clock when the device clock is unavailable or misbehaves. See <see cref="DeviceClockAnchor"/>.
    /// </param>
    /// <param name="latencyReporter">
    /// Optional sink for the resolved output latency and its provenance, so a display can tell a
    /// measurement from an estimate. Null in tests and wherever nothing is watching.
    /// </param>
    public WasapiAudioPlayer(
        ILogger<WasapiAudioPlayer> logger,
        string? deviceId = null,
        SyncCorrectionMechanism mechanism = SyncCorrectionMechanism.SmoothResampling,
        bool useDeviceClock = false,
        OutputLatencyReporter? latencyReporter = null)
    {
        _logger = logger;
        _deviceId = deviceId;
        _mechanism = mechanism;
        _useDeviceClock = useDeviceClock;
        _latencyReporter = latencyReporter;
    }

    /// <summary>
    /// Notifies the player that a WebSocket reconnect occurred.
    /// Forwards to the sync correction source to suppress corrections during Kalman re-convergence.
    /// </summary>
    public void NotifyReconnect()
    {
        _correctedSource?.NotifyReconnect();
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
                    if (device != null)
                    {
                        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                    }
                    else
                    {
                        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                    }

                    _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                    // Placeholder until SetSampleSource can query the client - the same estimate the
                    // ladder's bottom tier produces, so the two paths can never disagree. Querying
                    // before Init() throws AUDCLNT_E_NOT_INITIALIZED.
                    SetOutputLatency(EstimatedOutputLatency());

                    SetState(AudioPlayerState.Stopped);
                    _logger.LogInformation(
                        "WASAPI player initialized: {SampleRate}Hz {Channels}ch, latency: {Latency}ms ({Provenance}), device: {Device}",
                        format.SampleRate,
                        format.Channels,
                        OutputLatencyMs,
                        OutputLatencyProvenance.Estimated,
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

        DisposeCorrectionSource();

        // Get buffer from source for sync correction (if source is BufferedAudioSampleSource)
        _buffer = null;
        IAudioSampleSource effectiveSource = source;

        if (source is BufferedAudioSampleSource bufferedSource)
        {
            _buffer = bufferedSource.Buffer;

            // Sync correction is the SDK's job end to end: it drives ReadRaw, builds a
            // SyncCorrectionCalculator from the buffer's own SyncOptions (which carry the app's
            // configured mechanism, dead band and speed cap), applies the correction, and reports
            // the applied rate back to the buffer so the stats UI keeps reading a live value.
            _correctedSource = new SyncCorrectedSampleSource(
                _buffer,
                GetSyncTimeMicroseconds,
                logger: _logger);

            effectiveSource = _correctedSource;

            _logger.LogDebug(
                "Sync correction delegated to the SDK ({Mechanism})",
                _buffer.SyncOptions.Mechanism);
        }

        // Create NAudio adapter with current volume/mute state
        _sampleProvider = new AudioSampleProviderAdapter(effectiveSource, _format);
        _sampleProvider.Volume = _volume;
        _sampleProvider.IsMuted = _isMuted;

        _wasapiOut.Init(BuildOutputChain(_sampleProvider));

        // Now that Init() has initialized the underlying AudioClient, read the real device latency.
        // Querying earlier throws AUDCLNT_E_NOT_INITIALIZED and falls back to an estimate.
        SetOutputLatency(GetActualOutputLatency(_wasapiOut));
    }

    /// <summary>
    /// Puts device-rate conversion on top of the corrected stream, when the mechanism allows a
    /// resampler in the chain and the device's mixer runs at a different rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one piece of the old app-side chain that stays: WASAPI shared mode resamples
    /// everything to the mixer rate, so converting here means the audio crosses one known-good,
    /// filtered conversion instead of the Windows Audio Engine's. It is a device-format concern and
    /// deliberately outside the SDK's platform-neutral correction source.
    /// </para>
    /// <para>
    /// Skipped entirely when the rates already match — a second identity resampler pass on top of
    /// the correction source's would be pure cost — and under
    /// <see cref="SyncCorrectionMechanism.FrameStepping"/>, whose whole point is that no resampler
    /// sits in the output chain. <see cref="OutputFormat"/> reports the same split.
    /// </para>
    /// </remarks>
    private ISampleProvider BuildOutputChain(ISampleProvider correctedProvider)
    {
        if (_mechanism == SyncCorrectionMechanism.FrameStepping)
        {
            _logger.LogInformation("Output chain: frame stepping, no resampler in chain");
            return correctedProvider;
        }

        if (_deviceNativeSampleRate == correctedProvider.WaveFormat.SampleRate)
        {
            _logger.LogDebug(
                "Output chain: no device-rate conversion needed ({Rate}Hz matches the device mixer)",
                _deviceNativeSampleRate);
            return correctedProvider;
        }

        return new DeviceRateSampleProvider(correctedProvider, _deviceNativeSampleRate, _logger);
    }

    /// <summary>
    /// Disposes the current sync-corrected sample source.
    /// </summary>
    /// <remarks>
    /// Order matters: the WASAPI output must already be stopped, so no read is in flight when the
    /// source stops accepting them. The device-rate provider downstream holds nothing to release —
    /// it owns only a resampler and its own buffers — so dropping the reference is enough.
    /// </remarks>
    private void DisposeCorrectionSource()
    {
        _correctedSource?.Dispose();
        _correctedSource = null;
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
        => SwitchDeviceInternalAsync(deviceId, forceResume: false, cancellationToken);

    private Task SwitchDeviceInternalAsync(string? deviceId, bool forceResume, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    // Remember current state. Device-invalidation recovery passes forceResume: by then
                    // playback has faulted off Playing, but we still want to resume on the rebuilt output.
                    var wasPlaying = forceResume || State == AudioPlayerState.Playing;
                    var currentSampleProvider = _sampleProvider;

                    _logger.LogInformation(
                        "Switching audio device from {OldDevice} to {NewDevice}",
                        _deviceId ?? "System Default",
                        deviceId ?? "System Default");

                    // Stop and dispose current output
                    if (_wasapiOut != null)
                    {
                        _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                        try
                        {
                            _wasapiOut.Stop();
                        }
                        catch (Exception stopEx)
                        {
                            // A device-invalidated output throws on Stop(); it is already dead, ignore.
                            _logger.LogDebug(stopEx, "Ignoring error stopping previous audio output");
                        }

                        _wasapiOut.Dispose();
                        _wasapiOut = null;
                    }

                    // The cached IAudioClock belongs to the disposed device; drop it and re-anchor so
                    // the new device's clock baseline is picked up cleanly (handled again on Playing,
                    // but cleared here too in case the switch happens while stopped). Also clear the
                    // diagnostic baseline so the first [AudioClock] log after the switch doesn't
                    // compute a bogus delta against the old device's (much larger) position.
                    _audioClockClient = null;
                    _deviceClockAnchor.Reset();
                    _probeLastClockMicros = 0;
                    _probeLastWallMicros = 0;

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
                    if (device != null)
                    {
                        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                    }
                    else
                    {
                        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                    }

                    _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                    // Reset sync tracking to prevent timing discontinuities from triggering false corrections
                    if (_buffer is TimedAudioBuffer timedBuffer)
                    {
                        timedBuffer.ResetSyncTracking();
                    }

                    _correctedSource?.Reset();

                    // Re-attach the sample source, rebuilding the output chain against the new
                    // device's native rate (the old device-rate provider converted to the old rate).
                    if (currentSampleProvider != null)
                    {
                        _wasapiOut.Init(BuildOutputChain(currentSampleProvider));
                        _logger.LogDebug(
                            "Sample source re-attached to new device ({DeviceRate}Hz)",
                            _deviceNativeSampleRate);
                    }

                    // Read the real device latency now that Init() has initialized the AudioClient.
                    SetOutputLatency(GetActualOutputLatency(_wasapiOut));

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
                        OutputLatencyMs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to switch audio device");
                    if (!forceResume)
                    {
                        // During recovery (forceResume) the retry loop owns the terminal Error decision,
                        // so it can try again rather than the first failed rebuild going silent forever.
                        SetState(AudioPlayerState.Error);
                        ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to switch audio device", ex));
                    }

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

        DisposeCorrectionSource();
        _sampleProvider = null;

        SetState(AudioPlayerState.Uninitialized);

        await Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            // AUDCLNT_E_DEVICE_INVALIDATED: the output device was pulled out from under WASAPI
            // (default device changed, device disabled/unplugged/reformatted, exclusive grab).
            // Recoverable - re-initialize the output instead of dropping to a terminal Error state.
            if (e.Exception is System.Runtime.InteropServices.COMException com
                && com.HResult == DeviceInvalidatedHResult)
            {
                _logger.LogWarning(e.Exception, "Audio output device invalidated; attempting to recover");
                _ = TryRecoverFromDeviceInvalidationAsync();
                return;
            }

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
    /// Re-initializes the audio output after the device was invalidated, preserving buffered audio.
    /// Retries a few times with a short backoff so Windows can settle a new default device, then
    /// falls back to a terminal Error state. Reuses the device-switch rebuild path (which keeps the
    /// buffer and re-anchors timing), so playback resumes from the buffered audio rather than silence.
    /// </summary>
    private async Task TryRecoverFromDeviceInvalidationAsync()
    {
        // One recovery in flight: the error can fire repeatedly while a device is flapping.
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
        {
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= MaxDeviceRecoveryAttempts; attempt++)
            {
                // Back off increasingly so the OS has time to promote a new default device.
                await Task.Delay(DeviceRecoveryBaseDelayMs * attempt).ConfigureAwait(false);

                try
                {
                    await SwitchDeviceInternalAsync(_deviceId, forceResume: true).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Recovered audio output after device invalidation (attempt {Attempt}/{Max})",
                        attempt,
                        MaxDeviceRecoveryAttempts);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Audio recovery attempt {Attempt}/{Max} failed",
                        attempt,
                        MaxDeviceRecoveryAttempts);
                }
            }

            _logger.LogError("Audio output could not be recovered after {Max} attempts", MaxDeviceRecoveryAttempts);
            SetState(AudioPlayerState.Error);
            ErrorOccurred?.Invoke(this, new AudioPlayerError("Audio device invalidated and could not be recovered"));
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
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
    /// Queries audio capabilities for a device without initializing playback.
    /// </summary>
    /// <remarks>
    /// This static method can be called at startup to discover device capabilities
    /// before creating a player instance. Used to advertise supported formats to servers.
    /// </remarks>
    /// <param name="deviceId">Device ID, or null for system default.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>The device capabilities, or defaults (48kHz/16-bit) if query fails.</returns>
    public static AudioDeviceCapabilities QueryDeviceCapabilities(string? deviceId, ILogger? logger = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            var mixFormat = device.AudioClient.MixFormat;

            logger?.LogInformation(
                "Device capabilities: {SampleRate}Hz {BitDepth}-bit {Channels}ch",
                mixFormat.SampleRate,
                mixFormat.BitsPerSample,
                mixFormat.Channels);

            return new AudioDeviceCapabilities
            {
                NativeSampleRate = mixFormat.SampleRate,
                NativeBitDepth = mixFormat.BitsPerSample,
                Channels = mixFormat.Channels,
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to query device capabilities, using defaults (48kHz/16-bit)");
            return new AudioDeviceCapabilities(); // 48kHz/16-bit/2ch defaults
        }
    }

    /// <summary>
    /// Queries the initialized audio client for whatever it can say about output latency, and hands
    /// it to <see cref="ResolveOutputLatency"/> to be turned into a reading.
    /// </summary>
    /// <remarks>
    /// NAudio's WasapiOut does not expose the underlying AudioClient, so both figures are reached by
    /// reflection. Anything unavailable is passed along as zero and the ladder decides what that
    /// means - this method deliberately makes no judgement of its own, which keeps the policy in one
    /// testable place.
    /// </remarks>
    /// <param name="wasapiOut">The initialized WasapiOut instance to query.</param>
    /// <returns>The resolved latency and how it was obtained.</returns>
    private OutputLatencyReading GetActualOutputLatency(WasapiOut wasapiOut)
    {
        var audioClient = TryGetAudioClient(wasapiOut);
        if (audioClient == null)
        {
            return ResolveOutputLatency(0, 0, 0, _logger);
        }

        // Each probe is guarded separately: a driver that rejects StreamLatency must still be able
        // to supply a buffer size, which is the entire point of having a second measured tier.
        var streamLatency100Ns = TryProbe(() => audioClient.StreamLatency, "StreamLatency");
        var bufferFrames = (int)TryProbe(() => audioClient.BufferSize, "BufferSize");

        // BufferSize counts frames of the format the client was INITIALIZED with, which is not
        // necessarily the device's mix format: in shared mode NAudio passes our provider's format
        // through verbatim with AUTOCONVERTPCM and lets the engine convert. Under SmoothResampling
        // that provider is the device-rate chain, already at the device rate; under FrameStepping
        // it is the source, at the stream rate. Dividing by the device rate in the latter case would
        // report a quarter of the real latency for a 48 kHz stream on a 192 kHz device - the same
        // rate-domain confusion as the resampler defect this change set exists to fix.
        var bufferRate = wasapiOut.OutputWaveFormat?.SampleRate ?? _deviceNativeSampleRate;

        return ResolveOutputLatency(streamLatency100Ns, bufferFrames, bufferRate, _logger);
    }

    /// <summary>
    /// Reaches NAudio's private audio client by reflection.
    /// </summary>
    /// <param name="wasapiOut">The initialized WasapiOut instance.</param>
    /// <returns>The audio client, or <see langword="null"/> if it could not be reached.</returns>
    private AudioClient? TryGetAudioClient(WasapiOut wasapiOut)
    {
        try
        {
            var audioClientField = typeof(WasapiOut).GetField(
                "audioClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (audioClientField?.GetValue(wasapiOut) is AudioClient audioClient)
            {
                return audioClient;
            }

            _logger.LogWarning("WasapiOut's audioClient field was not reachable by reflection");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reach the WASAPI audio client by reflection");
        }

        return null;
    }

    /// <summary>
    /// Runs one latency probe, yielding 0 if the driver rejects it.
    /// </summary>
    /// <param name="probe">The property read to attempt.</param>
    /// <param name="name">The probe's name, for the log line.</param>
    /// <returns>The probed value, or 0 on failure.</returns>
    private long TryProbe(Func<long> probe, string name)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WASAPI {Probe} probe failed", name);
            return 0;
        }
    }

    /// <summary>
    /// Turns whatever the audio client was willing to report into an output latency plus its
    /// provenance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three tiers, most trustworthy first:
    /// </para>
    /// <list type="number">
    /// <item><c>IAudioClient.StreamLatency</c>, when it reports anything at all - measured.</item>
    /// <item>The initialized client's buffer frame count over the device rate - measured. Some
    /// devices (the 192 kHz DAC in issue #73 among them) report a zero stream latency but a
    /// perfectly good buffer size.</item>
    /// <item>The requested latency plus assumed engine overhead - a guess, and labelled as one.</item>
    /// </list>
    /// <para>
    /// A non-positive <c>StreamLatency</c> is a FAILED read, not a small number. It used to be run
    /// through <c>Math.Max(latencyMs, requestedLatencyMs)</c>, which laundered a zero into exactly
    /// the requested 100 ms - indistinguishable downstream from a device that really did report
    /// 100 ms, and logged at debug so nobody saw it happen.
    /// </para>
    /// </remarks>
    /// <param name="streamLatency100Ns">The client's reported stream latency in 100 ns units, or 0 if unavailable.</param>
    /// <param name="bufferFrames">The client's buffer size in frames, or 0 if unavailable.</param>
    /// <param name="bufferSampleRate">
    /// The rate <paramref name="bufferFrames"/> is counted in, in Hz - that is, the rate of the
    /// format the audio client was initialized with, which is not always the device's mix rate.
    /// </param>
    /// <param name="logger">Optional logger; the estimated tier logs a warning.</param>
    /// <returns>The resolved latency and how it was obtained.</returns>
    public static OutputLatencyReading ResolveOutputLatency(
        long streamLatency100Ns,
        int bufferFrames,
        int bufferSampleRate,
        ILogger? logger = null)
    {
        if (streamLatency100Ns > 0)
        {
            // Rounded, not truncated: truncation biases every reading ~0.5 ms low on average, and
            // this figure is subtracted from the sync error, so that bias lands as a constant offset -
            // a smaller instance of exactly the defect this ladder exists to remove.
            var latencyMs = (int)Math.Round(streamLatency100Ns / 10000.0, MidpointRounding.AwayFromZero);
            logger?.LogDebug(
                "Output latency from StreamLatency: {StreamLatency} (100ns units) = {LatencyMs}ms",
                streamLatency100Ns,
                latencyMs);
            return new OutputLatencyReading(latencyMs, OutputLatencyProvenance.StreamLatency);
        }

        if (bufferFrames > 0 && bufferSampleRate > 0)
        {
            var latencyMs = (int)Math.Round(bufferFrames * 1000.0 / bufferSampleRate, MidpointRounding.AwayFromZero);

            // Warning, not debug. The primary probe failed on this device, and the shipped default
            // log level is Warning, so at debug the fact would never reach a user's log - which is
            // how the 100 ms substitution in #73 stayed invisible for so long. The condition is
            // recovered rather than fatal, which is what Warning means in this codebase.
            logger?.LogWarning(
                "WASAPI StreamLatency reported nothing on this device; measured output latency from " +
                "the client buffer instead: {Frames} frames at {Rate}Hz = {LatencyMs}ms",
                bufferFrames,
                bufferSampleRate,
                latencyMs);
            return new OutputLatencyReading(latencyMs, OutputLatencyProvenance.DeviceBuffer);
        }

        var estimated = EstimatedOutputLatency();
        logger?.LogWarning(
            "Output latency could not be measured (StreamLatency {StreamLatency}, buffer {Frames} frames, " +
            "buffer rate {Rate}Hz); ESTIMATING {LatencyMs}ms from the requested {Requested}ms + " +
            "{Overhead}ms assumed engine overhead. Sync error is compensated with this number, so a " +
            "wrong estimate shows up as a constant offset against other players.",
            streamLatency100Ns,
            bufferFrames,
            bufferSampleRate,
            estimated.LatencyMs,
            RequestedLatencyMs,
            WindowsAudioEngineOverheadMs);

        return estimated;
    }

    /// <summary>
    /// The bottom tier of the latency ladder: the requested latency plus assumed engine overhead.
    /// </summary>
    /// <remarks>
    /// Shared by the pre-<c>Init()</c> placeholder and <see cref="ResolveOutputLatency"/>'s fallback
    /// so that a player which never manages a measurement reports one number throughout, rather than
    /// the 115 / 100 disagreement the two paths used to produce.
    /// </remarks>
    /// <returns>The estimated reading.</returns>
    private static OutputLatencyReading EstimatedOutputLatency() =>
        new(RequestedLatencyMs + WindowsAudioEngineOverheadMs, OutputLatencyProvenance.Estimated);

    /// <summary>
    /// Records a resolved output latency as the player's current value and publishes it.
    /// </summary>
    /// <param name="reading">The reading to adopt.</param>
    private void SetOutputLatency(OutputLatencyReading reading)
    {
        // One reading, published as one reference, so the value and the provenance can never be
        // read apart. Storing the milliseconds separately would reintroduce the split this ladder
        // exists to close: a caller could see a measured figure still labelled an estimate.
        Volatile.Write(ref _outputLatency, reading);
        _latencyReporter?.Report(reading);
    }

    /// <summary>
    /// Sync time source handed to <see cref="SyncCorrectedSampleSource"/> - the app's actual
    /// playback timing path, which is what the SDK's correction source measures error against.
    /// This delegate IS invoked per render read during
    /// playback, so it reads the WASAPI device clock (IAudioClock) and returns it anchored onto the
    /// wall-clock timeline via <see cref="DeviceClockAnchor"/> - timing sync against the DAC's own
    /// crystal instead of the system clock (issue #33). Falls back to the wall clock when the device
    /// clock is unavailable, when it misbehaves, or when <see cref="_useDeviceClock"/> is false.
    /// </summary>
    private long GetSyncTimeMicroseconds()
    {
        var wallMicros = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();

        if (!_useDeviceClock)
        {
            return wallMicros;
        }

        // One hardware read per call, reused for both the returned sync time and the diagnostics log.
        var deviceMicros = TryReadAudioClockMicroseconds();
        var syncMicros = _deviceClockAnchor.Resolve(deviceMicros, wallMicros);

        if (_deviceClockAnchor.JustEngaged)
        {
            _logger.LogInformation("[Timing] Device audio clock engaged as the sync-timing source (IAudioClock, shared mode)");
        }
        else if (_deviceClockAnchor.JustDisabled)
        {
            _logger.LogWarning("[Timing] Device audio clock went non-monotonic; reverted to wall-clock timing for this stream");
        }

        LogAudioClockDiagnosticsIfDue(deviceMicros, wallMicros);
        return syncMicros;
    }

    /// <summary>
    /// Periodically logs the device-clock-vs-wall-clock advance ratio (issue #33 diagnostics). Uses
    /// the reading already taken in <see cref="GetSyncTimeMicroseconds"/> - it does not re-read
    /// hardware. A ratio &gt; 1.0 means the DAC is running faster than the system clock (the drift
    /// the device clock now corrects for); ~1.0000 means the two clocks already agree on this box.
    /// </summary>
    private void LogAudioClockDiagnosticsIfDue(long? clockMicros, long wallMicros)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastClockProbeLogTicks);
        if (nowTicks - last < ClockProbeLogIntervalTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastClockProbeLogTicks, nowTicks, last) != last)
        {
            return;
        }

        if (clockMicros == null)
        {
            _logger.LogInformation("[AudioClock] not readable yet (awaiting playback / IAudioClock not ready) - on wall-clock fallback");
            return;
        }

        if (_probeLastWallMicros != 0)
        {
            var clockDeltaMs = (clockMicros.Value - _probeLastClockMicros) / 1000.0;
            var wallDeltaMs = (wallMicros - _probeLastWallMicros) / 1000.0;
            var ratio = wallDeltaMs > 0 ? clockDeltaMs / wallDeltaMs : 0;
            _logger.LogInformation(
                "[AudioClock] pos={PosMs:F1}ms; advanced {ClockDeltaMs:F0}ms over wall {WallDeltaMs:F0}ms (ratio {Ratio:F4}; >1 = DAC faster than system clock)",
                clockMicros.Value / 1000.0,
                clockDeltaMs,
                wallDeltaMs,
                ratio);
        }
        else
        {
            _logger.LogInformation(
                "[AudioClock] IAudioClock readable in shared mode: pos={PosMs:F1}ms (first reading)",
                clockMicros.Value / 1000.0);
        }

        _probeLastClockMicros = clockMicros.Value;
        _probeLastWallMicros = wallMicros;
    }

    /// <summary>
    /// Reads the WASAPI device clock position in microseconds via IAudioClock, or null if it is
    /// not available. seconds = position / frequency (unit-agnostic per the WASAPI contract).
    /// </summary>
    private long? TryReadAudioClockMicroseconds()
    {
        // Only query once playback is running: Play() has called the audio client's Init()+Start(),
        // so IAudioClock is valid. Querying earlier (the SDK's one-time timing-source check at
        // pipeline setup, before WasapiOut.Init()) throws AUDCLNT_E_NOT_INITIALIZED. We must NOT
        // permanently disable on that, or a pre-playback failure masks a clock that works in play.
        if (State != AudioPlayerState.Playing)
        {
            return null;
        }

        try
        {
            var clock = GetAudioClockClient();
            if (clock == null)
            {
                return null;
            }

            var frequency = clock.Frequency;
            if (frequency <= 0)
            {
                return null;
            }

            var position = clock.AdjustedPosition;
            return (long)((double)position / frequency * 1_000_000.0);
        }
        catch (Exception ex)
        {
            // Drop the cached client and retry on the next probe (also handles device-switch staleness).
            _audioClockClient = null;
            _logger.LogDebug(ex, "[AudioClockProbe] IAudioClock read threw; will retry");
            return null;
        }
    }

    /// <summary>
    /// Lazily resolves the <see cref="AudioClockClient"/> from WasapiOut's private AudioClient
    /// (the same reflection reach-in used for stream latency), available after Init().
    /// </summary>
    private AudioClockClient? GetAudioClockClient()
    {
        if (_audioClockClient != null)
        {
            return _audioClockClient;
        }

        if (_wasapiOut == null)
        {
            return null;
        }

        var audioClientField = typeof(WasapiOut).GetField(
            "audioClient",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (audioClientField?.GetValue(_wasapiOut) is AudioClient audioClient)
        {
            _audioClockClient = audioClient.AudioClockClient;
        }

        return _audioClockClient;
    }

    private void SetState(AudioPlayerState newState)
    {
        if (State != newState)
        {
            // Entering Playing means a (re)started WASAPI stream whose device-clock position zeroes;
            // re-anchor so the reset is taken as a fresh anchor, not mistaken for a backward glitch.
            if (newState == AudioPlayerState.Playing && State != AudioPlayerState.Playing)
            {
                _deviceClockAnchor.Reset();
            }

            _logger.LogDebug("Player state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
}
