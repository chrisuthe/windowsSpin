// <copyright file="StatsViewModel.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Synchronization;

namespace SendSpinClient.ViewModels;

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

    #endregion

    #region Clock Sync Properties

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

        // Correction Mode
        CorrectionModeDisplay = stats.CurrentCorrectionMode switch
        {
            SyncCorrectionMode.Dropping => "Dropping",
            SyncCorrectionMode.Inserting => "Inserting",
            _ => "None",
        };

        CorrectionModeColor = stats.CurrentCorrectionMode switch
        {
            SyncCorrectionMode.Dropping => new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)), // Yellow
            SyncCorrectionMode.Inserting => new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)), // Yellow
            _ => new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)), // Green
        };

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

        ClockOffsetDisplay = $"{status.OffsetMilliseconds:+0.00;-0.00} ms";
        ClockUncertaintyDisplay = $"{status.OffsetUncertaintyMicroseconds / 1000.0:F2} ms";

        // Convert drift from microseconds/second to parts per million (ppm)
        // 1 ppm = 1 microsecond per second
        var driftPpm = status.DriftMicrosecondsPerSecond;
        DriftRateDisplay = $"{driftPpm:+0.00;-0.00} ppm";

        MeasurementCount = status.MeasurementCount;
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
