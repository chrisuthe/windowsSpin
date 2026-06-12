// <copyright file="StatsViewModel.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Synchronization;
using SendspinClient.Configuration;
using SendspinClient.Services.Diagnostics;
namespace SendspinClient.ViewModels;

/// <summary>
/// ViewModel for the "Stats for Nerds" window, providing real-time audio sync diagnostics.
/// </summary>
/// <remarks>
/// This ViewModel polls the audio pipeline and clock synchronizer at regular intervals
/// to provide real-time visibility into sync performance. This is essential for
/// diagnosing audio glitches like pops/clicks that may indicate sync issues.
/// </remarks>
public partial class StatsViewModel : ViewModelBase
{
    private readonly IAudioPipeline _audioPipeline;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly ClientCapabilities _clientCapabilities;
    private readonly SyncHealthMonitor _syncHealthMonitor;
    private readonly DispatcherTimer _updateTimer;

    // Previous sync drop/insert counts, so Correction Mode can show the tier actually acting this
    // tick (counters advancing = drop/insert; otherwise playback rate off 1.0 = resampling).
    // Null while the pipeline is idle so the first tick after (re)start never sees a false delta.
    private long? _lastSyncDroppedForSync;
    private long? _lastSyncInsertedForSync;
    private const int UpdateIntervalMs = 100; // 10 updates per second

    // Shared design-system brushes (Resources/Styles/Colors.xaml) so stat colors match the
    // rest of the app instead of redefining their own palette. Resolved once, not per tick.
    private static readonly Brush StatusGood = AppBrush("SuccessBrush");
    private static readonly Brush StatusWarning = AppBrush("WarningBrush");
    private static readonly Brush StatusBad = AppBrush("ErrorBrush");
    private static readonly Brush StatusActive = AppBrush("AccentBrush");
    private static readonly Brush ValueNeutral = AppBrush("TextPrimaryBrush");
    private static readonly Brush ValueMuted = AppBrush("TextMutedBrush");

    #region Sync Status Properties

    /// <summary>
    /// Gets the current sync error display string (e.g., "+2.35 ms" or "-1.20 ms").
    /// </summary>
    [ObservableProperty]
    private string _syncErrorDisplay = "-- ms";

    /// <summary>
    /// Gets the color for the sync error display (green=good, yellow=warning, red=bad).
    /// </summary>
    [ObservableProperty]
    private Brush _syncErrorColor = ValueMuted;

    /// <summary>
    /// Gets the current correction mode display string.
    /// </summary>
    [ObservableProperty]
    private string _correctionModeDisplay = "None";

    /// <summary>
    /// Gets the color for the correction mode display.
    /// </summary>
    [ObservableProperty]
    private Brush _correctionModeColor = ValueMuted;

    /// <summary>
    /// Gets whether playback is currently active.
    /// </summary>
    [ObservableProperty]
    private string _isPlaybackActive = "No";

    /// <summary>
    /// Gets the sync health verdict line (latest episode classification, or all-clear).
    /// </summary>
    [ObservableProperty]
    private string _healthDisplay = "No issues detected";

    /// <summary>
    /// Gets the color for the health display (green all-clear, yellow when episodes exist).
    /// </summary>
    [ObservableProperty]
    private Brush _healthColor = ValueMuted;

    #endregion

    #region Buffer Properties

    /// <summary>
    /// Gets the current buffered time display.
    /// </summary>
    [ObservableProperty]
    private string _bufferedMsDisplay = "-- ms";

    /// <summary>
    /// Gets the target buffer time display.
    /// </summary>
    [ObservableProperty]
    private string _targetMsDisplay = "-- ms";

    /// <summary>
    /// Gets the underrun count.
    /// </summary>
    [ObservableProperty]
    private long _underrunCount;

    /// <summary>
    /// Gets the color for underrun count (red if > 0).
    /// </summary>
    [ObservableProperty]
    private Brush _underrunColor = ValueNeutral;

    /// <summary>
    /// Gets the overrun count.
    /// </summary>
    [ObservableProperty]
    private long _overrunCount;

    #endregion

    #region Sync Correction Properties

    /// <summary>
    /// Gets the number of samples dropped for sync correction.
    /// </summary>
    [ObservableProperty]
    private long _samplesDroppedForSync;

    /// <summary>
    /// Gets the number of samples inserted for sync correction.
    /// </summary>
    [ObservableProperty]
    private long _samplesInsertedForSync;

    /// <summary>
    /// Gets the number of samples dropped due to buffer overflow.
    /// </summary>
    [ObservableProperty]
    private long _droppedSamples;

    /// <summary>
    /// Gets the target playback rate display (e.g., "1.02x" for 2% speedup).
    /// </summary>
    [ObservableProperty]
    private string _playbackRateDisplay = "1.00x";

    /// <summary>
    /// Gets the color for the playback rate (cyan when actively correcting).
    /// </summary>
    [ObservableProperty]
    private Brush _playbackRateColor = ValueNeutral;

    #endregion

    #region Clock Sync Properties

    /// <summary>
    /// Gets the clock sync status display (e.g., "Synchronized", "Syncing...").
    /// </summary>
    [ObservableProperty]
    private string _clockSyncStatusDisplay = "Not synced";

    /// <summary>
    /// Gets the color for the clock sync status.
    /// </summary>
    [ObservableProperty]
    private Brush _clockSyncStatusColor = ValueMuted;

    /// <summary>
    /// Gets the clock offset display string.
    /// </summary>
    [ObservableProperty]
    private string _clockOffsetDisplay = "-- ms";

    /// <summary>
    /// Gets the clock uncertainty display string.
    /// </summary>
    [ObservableProperty]
    private string _clockUncertaintyDisplay = "-- ms";

    /// <summary>
    /// Gets the drift rate display string.
    /// </summary>
    [ObservableProperty]
    private string _driftRateDisplay = "-- ppm";

    /// <summary>
    /// Gets the measurement count.
    /// </summary>
    [ObservableProperty]
    private int _measurementCount;

    /// <summary>
    /// Gets whether drift compensation is reliable (low uncertainty).
    /// </summary>
    [ObservableProperty]
    private string _driftReliableDisplay = "No";

    /// <summary>
    /// Gets the color for the drift reliable indicator.
    /// </summary>
    [ObservableProperty]
    private Brush _driftReliableColor = ValueMuted;

    /// <summary>
    /// Gets the static delay display string.
    /// </summary>
    [ObservableProperty]
    private string _staticDelayDisplay = "0 ms";

    /// <summary>
    /// Gets the detected output latency display string.
    /// </summary>
    [ObservableProperty]
    private string _outputLatencyDisplay = "-- ms";

    #endregion

    #region Throughput Properties

    /// <summary>
    /// Gets the total samples written.
    /// </summary>
    [ObservableProperty]
    private string _totalSamplesWritten = "0";

    /// <summary>
    /// Gets the total samples read.
    /// </summary>
    [ObservableProperty]
    private string _totalSamplesRead = "0";

    #endregion

    #region Audio Format Properties

    /// <summary>
    /// Gets the incoming audio format display string (e.g., "OPUS 48000Hz 2ch @ 128kbps").
    /// </summary>
    /// <remarks>
    /// This represents the format of audio received from the server, before decoding.
    /// </remarks>
    [ObservableProperty]
    private string _inputFormatDisplay = "--";

    /// <summary>
    /// Gets the output audio format display string (e.g., "PCM 48000Hz 2ch 32bit").
    /// </summary>
    /// <remarks>
    /// This represents the format of audio being sent to the output device after decoding.
    /// </remarks>
    [ObservableProperty]
    private string _outputFormatDisplay = "--";

    /// <summary>
    /// Gets the input audio codec (e.g., "OPUS", "FLAC", "PCM").
    /// </summary>
    [ObservableProperty]
    private string _inputCodec = "--";

    /// <summary>
    /// Gets the output audio codec (e.g., "PCM" after decoding).
    /// </summary>
    [ObservableProperty]
    private string _outputCodec = "--";

    /// <summary>
    /// Gets the input sample rate display (e.g., "48000 Hz").
    /// </summary>
    [ObservableProperty]
    private string _inputSampleRateDisplay = "--";

    /// <summary>
    /// Gets the output sample rate display (e.g., "48000 Hz").
    /// </summary>
    [ObservableProperty]
    private string _outputSampleRateDisplay = "--";

    /// <summary>
    /// Gets the input channels display (e.g., "Stereo" or "2ch").
    /// </summary>
    [ObservableProperty]
    private string _inputChannelsDisplay = "--";

    /// <summary>
    /// Gets the output channels display (e.g., "Stereo" or "2ch").
    /// </summary>
    [ObservableProperty]
    private string _outputChannelsDisplay = "--";

    /// <summary>
    /// Gets the input bit depth or bitrate display (e.g., "128 kbps" for lossy or "24-bit" for PCM).
    /// </summary>
    [ObservableProperty]
    private string _inputBitrateDisplay = "--";

    /// <summary>
    /// Gets the output bit depth display (e.g., "32-bit float").
    /// </summary>
    [ObservableProperty]
    private string _outputBitDepthDisplay = "--";

    #endregion

    #region Advertised Capabilities Properties

    /// <summary>
    /// Gets the advertised codec preference order (e.g., "FLAC → Opus → PCM").
    /// </summary>
    [ObservableProperty]
    private string _advertisedCodecOrder = "--";

    /// <summary>
    /// Gets the advertised audio formats summary (e.g., "96kHz/24-bit, 48kHz/16-bit").
    /// </summary>
    [ObservableProperty]
    private string _advertisedFormatsDisplay = "--";

    /// <summary>
    /// Gets the device native capabilities (e.g., "96kHz/24-bit Hi-Res").
    /// </summary>
    [ObservableProperty]
    private string _deviceCapabilitiesDisplay = "--";

    /// <summary>
    /// Gets the color for the device capabilities display (green for hi-res, white otherwise).
    /// </summary>
    [ObservableProperty]
    private Brush _deviceCapabilitiesColor = ValueNeutral;

    #endregion

    /// <summary>
    /// Gets the update rate display string.
    /// </summary>
    [ObservableProperty]
    private string _updateRateDisplay = $"Updating every {UpdateIntervalMs}ms";

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsViewModel"/> class.
    /// </summary>
    /// <param name="audioPipeline">The audio pipeline to monitor.</param>
    /// <param name="clockSynchronizer">The clock synchronizer to monitor.</param>
    /// <param name="clientCapabilities">The client capabilities for advertised format display.</param>
    /// <param name="syncHealthMonitor">The sync health monitor for episode diagnostics.</param>
    public StatsViewModel(
        IAudioPipeline audioPipeline,
        IClockSynchronizer clockSynchronizer,
        ClientCapabilities clientCapabilities,
        SyncHealthMonitor syncHealthMonitor)
    {
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        _clientCapabilities = clientCapabilities;
        _syncHealthMonitor = syncHealthMonitor;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs),
        };
        _updateTimer.Tick += OnUpdateTimerTick;

        // Initialize advertised capabilities display
        UpdateAdvertisedCapabilities();
    }

    /// <summary>
    /// Starts polling for stats updates.
    /// </summary>
    public void StartPolling()
    {
        _updateTimer.Start();
        UpdateStats(); // Immediate first update
    }

    /// <summary>
    /// Stops polling for stats updates.
    /// </summary>
    public void StopPolling()
    {
        _updateTimer.Stop();
    }

    private void OnUpdateTimerTick(object? sender, EventArgs e)
    {
        UpdateStats();
    }

    private void UpdateStats()
    {
        UpdateBufferStats();
        UpdateClockSyncStats();
        UpdateAudioFormatStats();
        UpdateHealthStats();
    }

    private void UpdateHealthStats()
    {
        HealthDisplay = _syncHealthMonitor.HealthDisplay;
        HealthColor = _syncHealthMonitor.EpisodeCount == 0
            ? StatusGood  // Green
            : StatusWarning; // Yellow
    }

    private void UpdateBufferStats()
    {
        var stats = _audioPipeline.BufferStats;
        if (stats == null)
        {
            // Pipeline not active
            SyncErrorDisplay = "-- ms";
            SyncErrorColor = ValueMuted;
            CorrectionModeDisplay = "Idle";
            CorrectionModeColor = ValueMuted;
            IsPlaybackActive = "No";
            BufferedMsDisplay = "-- ms";
            TargetMsDisplay = "-- ms";
            _lastSyncDroppedForSync = null;
            _lastSyncInsertedForSync = null;
            return;
        }

        // Sync Error
        var syncErrorMs = stats.SyncErrorMs;
        SyncErrorDisplay = $"{syncErrorMs:+0.00;-0.00} ms";

        var absError = Math.Abs(syncErrorMs);
        if (absError < 5)
        {
            SyncErrorColor = StatusGood; // Green
        }
        else if (absError < 20)
        {
            SyncErrorColor = StatusWarning; // Yellow
        }
        else
        {
            SyncErrorColor = StatusBad; // Red
        }

        // Correction Mode - show the tier actually acting this tick. The shipped strategy is
        // "Combined": small errors are trimmed by smooth resampling (playback-rate adjust), and
        // only larger errors fall back to dropping/inserting frames. Detect drop/insert by the
        // counters advancing since the last tick, and resampling by the playback rate being off 1.0.
        var droppedDelta = _lastSyncDroppedForSync is { } lastDropped
            ? Math.Max(0, stats.SamplesDroppedForSync - lastDropped)
            : 0;
        var insertedDelta = _lastSyncInsertedForSync is { } lastInserted
            ? Math.Max(0, stats.SamplesInsertedForSync - lastInserted)
            : 0;
        _lastSyncDroppedForSync = stats.SamplesDroppedForSync;
        _lastSyncInsertedForSync = stats.SamplesInsertedForSync;

        // Cancelling typical clock drift only needs ~50 ppm of rate trim, so use a fine epsilon.
        // The old 0.001 (1000 ppm) was far too coarse to ever register real resampling.
        var resamplingActive = Math.Abs(stats.TargetPlaybackRate - 1.0) > 0.00002;

        if (droppedDelta > 0)
        {
            CorrectionModeDisplay = "Dropping";
            CorrectionModeColor = StatusWarning; // Yellow
        }
        else if (insertedDelta > 0)
        {
            CorrectionModeDisplay = "Inserting";
            CorrectionModeColor = StatusWarning; // Yellow
        }
        else if (resamplingActive)
        {
            CorrectionModeDisplay = "Resampling";
            CorrectionModeColor = StatusActive; // Blue
        }
        else
        {
            CorrectionModeDisplay = "None";
            CorrectionModeColor = StatusGood; // Green
        }

        // Playback Rate (for resampling-based sync correction). F5 so sub-0.1% trims are visible
        // (e.g. 1.00005x); F3 rounded every correction down to a flat "1.000x".
        var rate = stats.TargetPlaybackRate;
        PlaybackRateDisplay = $"{rate:F5}x";
        PlaybackRateColor = Math.Abs(rate - 1.0) > 0.00002
            ? StatusActive // Blue when actively adjusting
            : ValueNeutral;

        // Playback state
        IsPlaybackActive = stats.IsPlaybackActive ? "Yes" : "No";

        // Buffer levels
        BufferedMsDisplay = $"{stats.BufferedMs:F0} ms";
        TargetMsDisplay = $"{stats.TargetMs:F0} ms";

        // Underruns/Overruns
        UnderrunCount = stats.UnderrunCount;
        UnderrunColor = stats.UnderrunCount > 0
            ? StatusBad
            : ValueNeutral;
        OverrunCount = stats.OverrunCount;

        // Sync correction stats
        SamplesDroppedForSync = stats.SamplesDroppedForSync;
        SamplesInsertedForSync = stats.SamplesInsertedForSync;
        DroppedSamples = stats.DroppedSamples;

        // Throughput
        TotalSamplesWritten = FormatSampleCount(stats.TotalSamplesWritten);
        TotalSamplesRead = FormatSampleCount(stats.TotalSamplesRead);
    }

    private void UpdateClockSyncStats()
    {
        var status = _clockSynchronizer.GetStatus();

        // Sync status indicator
        if (status.IsConverged)
        {
            ClockSyncStatusDisplay = "✓ Synchronized";
            ClockSyncStatusColor = StatusGood; // Green
        }
        else if (status.MeasurementCount > 0)
        {
            ClockSyncStatusDisplay = "Syncing...";
            ClockSyncStatusColor = StatusWarning; // Yellow
        }
        else
        {
            ClockSyncStatusDisplay = "Not synced";
            ClockSyncStatusColor = ValueMuted;
        }

        ClockOffsetDisplay = $"{status.OffsetMilliseconds:+0.00;-0.00} ms";
        ClockUncertaintyDisplay = $"{status.OffsetUncertaintyMicroseconds / 1000.0:F2} ms";

        // Convert drift from microseconds/second to parts per million (ppm)
        // 1 ppm = 1 microsecond per second
        var driftPpm = status.DriftMicrosecondsPerSecond;
        DriftRateDisplay = $"{driftPpm:+0.00;-0.00} ppm";

        MeasurementCount = status.MeasurementCount;

        // Drift reliability indicator - shows when drift compensation is active
        if (status.IsDriftReliable)
        {
            DriftReliableDisplay = "✓ Yes";
            DriftReliableColor = StatusGood; // Green
        }
        else if (status.MeasurementCount > 0)
        {
            DriftReliableDisplay = "Calibrating...";
            DriftReliableColor = StatusWarning; // Yellow
        }
        else
        {
            DriftReliableDisplay = "No";
            DriftReliableColor = ValueMuted;
        }

        // Static delay (from clock synchronizer)
        StaticDelayDisplay = $"{_clockSynchronizer.StaticDelayMs:+0;-0;0} ms";

        // Detected output latency (from audio pipeline)
        var detectedLatency = _audioPipeline.DetectedOutputLatencyMs;
        OutputLatencyDisplay = detectedLatency > 0 ? $"{detectedLatency} ms" : "-- ms";
    }

    /// <summary>
    /// Resolves a brush from the application's shared style resources.
    /// Falls back to a plain gray brush when resources are unavailable (e.g. design time).
    /// </summary>
    /// <param name="key">The resource key from Resources/Styles/Colors.xaml.</param>
    /// <returns>The shared brush, or a gray fallback.</returns>
    private static Brush AppBrush(string key) =>
        System.Windows.Application.Current?.Resources[key] as Brush ?? new SolidColorBrush(Colors.Gray);

    private static string FormatSampleCount(long samples)
    {
        if (samples >= 1_000_000_000)
        {
            return $"{samples / 1_000_000_000.0:F2}B";
        }
        else if (samples >= 1_000_000)
        {
            return $"{samples / 1_000_000.0:F2}M";
        }
        else if (samples >= 1_000)
        {
            return $"{samples / 1_000.0:F1}K";
        }

        return samples.ToString();
    }

    private void UpdateAudioFormatStats()
    {
        var inputFormat = _audioPipeline.CurrentFormat;
        var outputFormat = _audioPipeline.OutputFormat;

        // Update input format properties
        if (inputFormat != null)
        {
            InputFormatDisplay = inputFormat.ToString();
            InputCodec = inputFormat.Codec.ToUpperInvariant();
            InputSampleRateDisplay = $"{inputFormat.SampleRate} Hz";
            InputChannelsDisplay = FormatChannels(inputFormat.Channels);
            InputBitrateDisplay = FormatBitrateOrBitDepth(inputFormat);
        }
        else
        {
            InputFormatDisplay = "--";
            InputCodec = "--";
            InputSampleRateDisplay = "--";
            InputChannelsDisplay = "--";
            InputBitrateDisplay = "--";
        }

        // Update output format properties
        if (outputFormat != null)
        {
            OutputFormatDisplay = FormatOutputFormat(outputFormat);
            OutputCodec = "PCM"; // After decoding, all audio is PCM
            OutputSampleRateDisplay = $"{outputFormat.SampleRate} Hz";
            OutputChannelsDisplay = FormatChannels(outputFormat.Channels);
            OutputBitDepthDisplay = "32-bit float"; // NAudio uses 32-bit float internally
        }
        else
        {
            OutputFormatDisplay = "--";
            OutputCodec = "--";
            OutputSampleRateDisplay = "--";
            OutputChannelsDisplay = "--";
            OutputBitDepthDisplay = "--";
        }
    }

    /// <summary>
    /// Updates the advertised capabilities display from client capabilities.
    /// </summary>
    private void UpdateAdvertisedCapabilities()
    {
        var formats = _clientCapabilities.AudioFormats;
        if (formats == null || formats.Count == 0)
        {
            AdvertisedCodecOrder = "--";
            AdvertisedFormatsDisplay = "--";
            DeviceCapabilitiesDisplay = "--";
            return;
        }

        // Use the helper methods from AudioFormatBuilder
        AdvertisedCodecOrder = AudioFormatBuilder.GetCodecOrderDisplay(formats);
        AdvertisedFormatsDisplay = AudioFormatBuilder.GetFormatsDisplay(formats);

        // Device native is the first format's resolution (highest priority = native)
        var first = formats.First();
        var isHiRes = first.SampleRate > 48000 || (first.BitDepth ?? 16) > 16;
        var hiResLabel = isHiRes ? " Hi-Res" : "";
        DeviceCapabilitiesDisplay = $"{first.SampleRate / 1000.0:0.#}kHz/{first.BitDepth ?? 16}-bit{hiResLabel}";

        // Green for hi-res, white for standard
        DeviceCapabilitiesColor = isHiRes
            ? StatusGood // Green
            : ValueNeutral;
    }

    /// <summary>
    /// Formats the number of channels as a human-readable string.
    /// </summary>
    /// <param name="channels">Number of audio channels.</param>
    /// <returns>Human-readable channel description (e.g., "Mono", "Stereo", "5.1").</returns>
    private static string FormatChannels(int channels)
    {
        return channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{channels}ch",
        };
    }

    /// <summary>
    /// Formats bitrate (for lossy codecs) or bit depth (for lossless/PCM).
    /// </summary>
    /// <param name="format">The audio format.</param>
    /// <returns>Formatted bitrate or bit depth string.</returns>
    private static string FormatBitrateOrBitDepth(Sendspin.SDK.Models.AudioFormat format)
    {
        if (format.Bitrate.HasValue)
        {
            return $"{format.Bitrate} kbps";
        }
        else if (format.BitDepth.HasValue)
        {
            return $"{format.BitDepth}-bit";
        }

        // For Opus without bitrate info, show as variable
        if (format.Codec.Equals("opus", StringComparison.OrdinalIgnoreCase))
        {
            return "VBR";
        }

        return "--";
    }

    /// <summary>
    /// Formats the output format for display, emphasizing that it's decoded PCM.
    /// </summary>
    /// <param name="format">The output audio format.</param>
    /// <returns>Formatted output format string.</returns>
    private static string FormatOutputFormat(Sendspin.SDK.Models.AudioFormat format)
    {
        // Output is always decoded to 32-bit float PCM for NAudio
        return $"PCM {format.SampleRate}Hz {format.Channels}ch 32-bit float";
    }

}
