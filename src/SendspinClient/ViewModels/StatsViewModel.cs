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
    private readonly IDiagnosticAudioRecorder _diagnosticRecorder;
    private readonly ClientCapabilities _clientCapabilities;
    private readonly DispatcherTimer _updateTimer;
    private const int UpdateIntervalMs = 100; // 10 updates per second
    private bool _isSaving;

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
    private Brush _syncErrorColor = Brushes.Gray;

    /// <summary>
    /// Gets the current correction mode display string.
    /// </summary>
    [ObservableProperty]
    private string _correctionModeDisplay = "None";

    /// <summary>
    /// Gets the color for the correction mode display.
    /// </summary>
    [ObservableProperty]
    private Brush _correctionModeColor = Brushes.Gray;

    /// <summary>
    /// Gets whether playback is currently active.
    /// </summary>
    [ObservableProperty]
    private string _isPlaybackActive = "No";

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
    private Brush _underrunColor = Brushes.White;

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
    private Brush _playbackRateColor = Brushes.White;

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
    private Brush _clockSyncStatusColor = Brushes.Gray;

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
    private Brush _driftReliableColor = Brushes.Gray;

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

    /// <summary>
    /// Gets the expected RTT display string.
    /// Only shown when RTT tracking is enabled.
    /// </summary>
    [ObservableProperty]
    private string _expectedRttDisplay = "-- μs";

    /// <summary>
    /// Gets whether RTT tracking is reliable.
    /// </summary>
    [ObservableProperty]
    private string _rttReliableDisplay = "N/A";

    /// <summary>
    /// Gets the color for the RTT reliable indicator.
    /// </summary>
    [ObservableProperty]
    private Brush _rttReliableColor = Brushes.Gray;

    /// <summary>
    /// Gets the drift acceleration display string.
    /// Only shown when acceleration tracking is enabled.
    /// </summary>
    [ObservableProperty]
    private string _driftAccelerationDisplay = "-- μs/s²";

    /// <summary>
    /// Gets the number of adaptive forgetting events triggered by offset errors.
    /// </summary>
    [ObservableProperty]
    private int _adaptiveForgettingCount;

    /// <summary>
    /// Gets the number of network change events detected via RTT shifts.
    /// </summary>
    [ObservableProperty]
    private int _networkChangeCount;

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
    private Brush _deviceCapabilitiesColor = Brushes.White;

    #endregion

    #region Diagnostic Recording Properties

    /// <summary>
    /// Gets or sets whether diagnostic recording is enabled.
    /// When enabled, allocates ~17MB for a 45-second circular buffer.
    /// </summary>
    [ObservableProperty]
    private bool _isDiagnosticRecordingEnabled;

    /// <summary>
    /// Gets the recording status display string.
    /// </summary>
    [ObservableProperty]
    private string _recordingStatusDisplay = "Off";

    /// <summary>
    /// Gets the color for the recording status.
    /// </summary>
    [ObservableProperty]
    private Brush _recordingStatusColor = Brushes.Gray;

    /// <summary>
    /// Gets whether saving is available (recording is enabled and has data).
    /// </summary>
    [ObservableProperty]
    private bool _canSaveRecording;

    /// <summary>
    /// Gets the buffer duration in seconds for display.
    /// </summary>
    [ObservableProperty]
    private string _bufferDurationDisplay = "45s";

    /// <summary>
    /// Gets whether a save operation is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isSavingRecording;

    /// <summary>
    /// Gets the path to the most recently saved recording.
    /// </summary>
    [ObservableProperty]
    private string? _lastSavedRecordingPath;

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
    /// <param name="diagnosticRecorder">The diagnostic audio recorder.</param>
    /// <param name="clientCapabilities">The client capabilities for advertised format display.</param>
    public StatsViewModel(
        IAudioPipeline audioPipeline,
        IClockSynchronizer clockSynchronizer,
        IDiagnosticAudioRecorder diagnosticRecorder,
        ClientCapabilities clientCapabilities)
    {
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        _diagnosticRecorder = diagnosticRecorder;
        _clientCapabilities = clientCapabilities;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs),
        };
        _updateTimer.Tick += OnUpdateTimerTick;

        // Initialize diagnostic recording display
        BufferDurationDisplay = $"{_diagnosticRecorder.BufferDurationSeconds}s buffer";
        UpdateRecordingStatus();

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
        UpdateRecordingStatus();

        // Record sync metrics for diagnostic correlation if enabled
        var stats = _audioPipeline.BufferStats;
        if (stats != null)
        {
            _diagnosticRecorder.RecordMetrics(stats);
        }
    }

    private void UpdateBufferStats()
    {
        var stats = _audioPipeline.BufferStats;
        if (stats == null)
        {
            // Pipeline not active
            SyncErrorDisplay = "-- ms";
            SyncErrorColor = Brushes.Gray;
            CorrectionModeDisplay = "Idle";
            CorrectionModeColor = Brushes.Gray;
            IsPlaybackActive = "No";
            BufferedMsDisplay = "-- ms";
            TargetMsDisplay = "-- ms";
            return;
        }

        // Sync Error
        var syncErrorMs = stats.SyncErrorMs;
        SyncErrorDisplay = $"{syncErrorMs:+0.00;-0.00} ms";

        var absError = Math.Abs(syncErrorMs);
        if (absError < 5)
        {
            SyncErrorColor = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)); // Green
        }
        else if (absError < 20)
        {
            SyncErrorColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }
        else
        {
            SyncErrorColor = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)); // Red
        }

        // Correction Mode - infer from smoothed error magnitude
        // Using DropInsertOnly strategy - no resampling, just drop/insert based on error
        // TODO: Make deadband configurable via appsettings
        const long DeadbandMicroseconds = 2_000; // 2ms deadband
        var absSmoothedError = Math.Abs(stats.SmoothedSyncErrorMicroseconds);
        string correctionModeText;
        Brush correctionColor;

        if (absSmoothedError < DeadbandMicroseconds)
        {
            correctionModeText = "None";
            correctionColor = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)); // Green
        }
        else if (stats.SmoothedSyncErrorMicroseconds > 0)
        {
            correctionModeText = "Dropping";
            correctionColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }
        else
        {
            correctionModeText = "Inserting";
            correctionColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }

        CorrectionModeDisplay = correctionModeText;
        CorrectionModeColor = correctionColor;

        // Playback Rate (for resampling-based sync correction)
        var rate = stats.TargetPlaybackRate;
        PlaybackRateDisplay = $"{rate:F3}x";
        PlaybackRateColor = Math.Abs(rate - 1.0) > 0.001
            ? new SolidColorBrush(Color.FromRgb(0x60, 0xa5, 0xfa)) // Blue when actively adjusting
            : Brushes.White;

        // Playback state
        IsPlaybackActive = stats.IsPlaybackActive ? "Yes" : "No";

        // Buffer levels
        BufferedMsDisplay = $"{stats.BufferedMs:F0} ms";
        TargetMsDisplay = $"{stats.TargetMs:F0} ms";

        // Underruns/Overruns
        UnderrunCount = stats.UnderrunCount;
        UnderrunColor = stats.UnderrunCount > 0
            ? new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71))
            : Brushes.White;
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
            ClockSyncStatusColor = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)); // Green
        }
        else if (status.MeasurementCount > 0)
        {
            ClockSyncStatusDisplay = "Syncing...";
            ClockSyncStatusColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }
        else
        {
            ClockSyncStatusDisplay = "Not synced";
            ClockSyncStatusColor = Brushes.Gray;
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
            DriftReliableColor = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)); // Green
        }
        else if (status.MeasurementCount > 0)
        {
            DriftReliableDisplay = "Calibrating...";
            DriftReliableColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }
        else
        {
            DriftReliableDisplay = "No";
            DriftReliableColor = Brushes.Gray;
        }

        // Static delay (from clock synchronizer)
        StaticDelayDisplay = $"{_clockSynchronizer.StaticDelayMs:+0;-0;0} ms";

        // Detected output latency (from audio pipeline)
        var detectedLatency = _audioPipeline.DetectedOutputLatencyMs;
        OutputLatencyDisplay = detectedLatency > 0 ? $"{detectedLatency} ms" : "-- ms";

        // 4D Kalman filter extended state (shown when enabled)
        // Expected RTT
        if (status.ExpectedRttMicroseconds > 0)
        {
            ExpectedRttDisplay = $"{status.ExpectedRttMicroseconds:F0} μs (±{status.RttUncertaintyMicroseconds:F0})";
        }
        else
        {
            ExpectedRttDisplay = "-- μs";
        }

        // RTT reliability indicator
        if (status.IsRttReliable)
        {
            RttReliableDisplay = "✓ Yes";
            RttReliableColor = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)); // Green
        }
        else if (status.ExpectedRttMicroseconds > 0)
        {
            RttReliableDisplay = "Calibrating...";
            RttReliableColor = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
        }
        else
        {
            RttReliableDisplay = "Disabled";
            RttReliableColor = Brushes.Gray;
        }

        // Drift acceleration (for thermal tracking)
        var accel = status.DriftAccelerationMicrosecondsPerSecondSquared;
        if (Math.Abs(accel) > 0.001)
        {
            DriftAccelerationDisplay = $"{accel:+0.000;-0.000} μs/s²";
        }
        else
        {
            DriftAccelerationDisplay = "0 μs/s²";
        }

        // Adaptive forgetting counters
        AdaptiveForgettingCount = status.AdaptiveForgettingTriggerCount;
        NetworkChangeCount = status.NetworkChangeTriggerCount;
    }

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
            ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)) // Green
            : Brushes.White;
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

    #region Diagnostic Recording Methods

    /// <summary>
    /// Updates the recording status display.
    /// </summary>
    private void UpdateRecordingStatus()
    {
        if (_isSaving)
        {
            RecordingStatusDisplay = "Saving...";
            RecordingStatusColor = new SolidColorBrush(Color.FromRgb(0x60, 0xa5, 0xfa)); // Blue
            CanSaveRecording = false;
            return;
        }

        if (!_diagnosticRecorder.IsEnabled)
        {
            RecordingStatusDisplay = "Off";
            RecordingStatusColor = Brushes.Gray;
            CanSaveRecording = false;
            return;
        }

        var bufferedSeconds = _diagnosticRecorder.BufferedSeconds;
        RecordingStatusDisplay = $"Recording ({bufferedSeconds:F0}s)";
        RecordingStatusColor = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)); // Red (recording)
        CanSaveRecording = bufferedSeconds > 0;
    }

    /// <summary>
    /// Called when IsDiagnosticRecordingEnabled changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnIsDiagnosticRecordingEnabledChanged(bool value)
    {
        if (value)
        {
            // Enable recording - need to get format from pipeline
            var outputFormat = _audioPipeline.OutputFormat;
            if (outputFormat != null)
            {
                _diagnosticRecorder.Enable(outputFormat.SampleRate, outputFormat.Channels);
            }
            else
            {
                // Default to 48kHz stereo if no format available yet
                _diagnosticRecorder.Enable(48000, 2);
            }
        }
        else
        {
            _diagnosticRecorder.Disable();
        }

        UpdateRecordingStatus();
    }

    /// <summary>
    /// Saves the current diagnostic recording to a WAV file.
    /// </summary>
    [RelayCommand]
    private async Task SaveDiagnosticRecordingAsync()
    {
        if (_isSaving || !_diagnosticRecorder.IsEnabled)
        {
            return;
        }

        _isSaving = true;
        IsSavingRecording = true;
        UpdateRecordingStatus();

        try
        {
            AppPaths.EnsureDiagnosticsDirectoryExists();
            var path = await _diagnosticRecorder.SaveAsync(AppPaths.DiagnosticsDirectory);
            LastSavedRecordingPath = path;
        }
        finally
        {
            _isSaving = false;
            IsSavingRecording = false;
            UpdateRecordingStatus();
        }
    }

    /// <summary>
    /// Opens the diagnostics folder in Windows Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenDiagnosticsFolder()
    {
        AppPaths.EnsureDiagnosticsDirectoryExists();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppPaths.DiagnosticsDirectory,
            UseShellExecute = true,
        });
    }

    #endregion
}
