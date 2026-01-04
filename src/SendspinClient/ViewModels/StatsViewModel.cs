// <copyright file="StatsViewModel.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Synchronization;

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
    private readonly DispatcherTimer _updateTimer;
    private const int UpdateIntervalMs = 100; // 10 updates per second

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
    public StatsViewModel(IAudioPipeline audioPipeline, IClockSynchronizer clockSynchronizer)
    {
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs),
        };
        _updateTimer.Tick += OnUpdateTimerTick;
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

        // Correction Mode - now with tiered strategy
        CorrectionModeDisplay = stats.CurrentCorrectionMode switch
        {
            SyncCorrectionMode.Resampling => "Resampling",
            SyncCorrectionMode.Dropping => "Dropping",
            SyncCorrectionMode.Inserting => "Inserting",
            _ => "None",
        };

        CorrectionModeColor = stats.CurrentCorrectionMode switch
        {
            SyncCorrectionMode.Resampling => new SolidColorBrush(Color.FromRgb(0x60, 0xa5, 0xfa)), // Blue (smooth)
            SyncCorrectionMode.Dropping => new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)), // Yellow
            SyncCorrectionMode.Inserting => new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)), // Yellow
            _ => new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)), // Green
        };

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
}
