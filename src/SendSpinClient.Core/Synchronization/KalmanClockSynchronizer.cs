using Microsoft.Extensions.Logging;

namespace SendSpinClient.Core.Synchronization;

/// <summary>
/// High-precision clock synchronizer using a 2D Kalman filter.
/// Tracks both clock offset and drift rate for accurate audio synchronization.
///
/// The Kalman filter state vector is [offset, drift]:
/// - offset: difference between server and client clocks (server_time = client_time + offset)
/// - drift: rate of change of offset (microseconds per second)
///
/// This approach handles network jitter by statistically filtering measurements
/// while also tracking and compensating for clock drift over time.
/// </summary>
public sealed class KalmanClockSynchronizer : IClockSynchronizer
{
    private readonly ILogger<KalmanClockSynchronizer>? _logger;
    private readonly object _lock = new();

    // Kalman filter state
    private double _offset;           // Estimated offset in microseconds
    private double _drift;            // Estimated drift in microseconds per second
    private double _offsetVariance;   // Uncertainty in offset estimate
    private double _driftVariance;    // Uncertainty in drift estimate
    private double _covariance;       // Cross-covariance between offset and drift

    // Timing
    private long _lastUpdateTime;     // Last measurement time in microseconds
    private int _measurementCount;

    // Configuration
    private readonly double _processNoiseOffset;   // How much offset can change per second
    private readonly double _processNoiseDrift;    // How much drift rate can change per second
    private readonly double _measurementNoise;     // Expected measurement noise (RTT variance)

    // Convergence tracking
    private const int MinMeasurementsForConvergence = 5;
    private const double MaxOffsetUncertaintyForConvergence = 1000.0; // 1ms uncertainty threshold

    /// <summary>
    /// Current estimated clock offset in microseconds.
    /// server_time = client_time + Offset
    /// </summary>
    public double Offset
    {
        get { lock (_lock) return _offset; }
    }

    /// <summary>
    /// Current estimated clock drift in microseconds per second.
    /// Positive means server clock is running faster than client.
    /// </summary>
    public double Drift
    {
        get { lock (_lock) return _drift; }
    }

    /// <summary>
    /// Uncertainty (standard deviation) of the offset estimate in microseconds.
    /// </summary>
    public double OffsetUncertainty
    {
        get { lock (_lock) return Math.Sqrt(_offsetVariance); }
    }

    /// <summary>
    /// Number of measurements processed.
    /// </summary>
    public int MeasurementCount
    {
        get { lock (_lock) return _measurementCount; }
    }

    /// <summary>
    /// Whether the synchronizer has converged to a stable estimate.
    /// </summary>
    public bool IsConverged
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForConvergence
                       && Math.Sqrt(_offsetVariance) < MaxOffsetUncertaintyForConvergence;
            }
        }
    }

    /// <summary>
    /// Creates a new Kalman clock synchronizer with default parameters.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="processNoiseOffset">Process noise for offset (default: 100 μs²/s).</param>
    /// <param name="processNoiseDrift">Process noise for drift (default: 1 μs²/s²).</param>
    /// <param name="measurementNoise">Measurement noise variance (default: 10000 μs², ~3ms std dev).</param>
    public KalmanClockSynchronizer(
        ILogger<KalmanClockSynchronizer>? logger = null,
        double processNoiseOffset = 100.0,
        double processNoiseDrift = 1.0,
        double measurementNoise = 10000.0)
    {
        _logger = logger;
        _processNoiseOffset = processNoiseOffset;
        _processNoiseDrift = processNoiseDrift;
        _measurementNoise = measurementNoise;

        Reset();
    }

    /// <summary>
    /// Resets the synchronizer to initial state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _offset = 0;
            _drift = 0;
            _offsetVariance = 1e12;  // Start with very high uncertainty (1 second)
            _driftVariance = 1e6;    // 1000 μs/s uncertainty
            _covariance = 0;
            _lastUpdateTime = 0;
            _measurementCount = 0;
        }

        _logger?.LogDebug("Clock synchronizer reset");
    }

    /// <summary>
    /// Processes a complete time exchange measurement.
    /// </summary>
    /// <param name="t1">Client transmit time (T1) in microseconds.</param>
    /// <param name="t2">Server receive time (T2) in microseconds.</param>
    /// <param name="t3">Server transmit time (T3) in microseconds.</param>
    /// <param name="t4">Client receive time (T4) in microseconds.</param>
    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
        // Calculate offset using NTP formula
        // offset = ((T2 - T1) + (T3 - T4)) / 2
        double measuredOffset = ((t2 - t1) + (t3 - t4)) / 2.0;

        // Round-trip time for quality assessment
        // RTT = (T4 - T1) - (T3 - T2)
        double rtt = (t4 - t1) - (t3 - t2);

        // Server processing time
        double serverProcessing = t3 - t2;

        lock (_lock)
        {
            // First measurement: initialize state
            if (_measurementCount == 0)
            {
                _offset = measuredOffset;
                _lastUpdateTime = t4;
                _measurementCount = 1;

                _logger?.LogDebug(
                    "Initial time sync: offset={Offset:F0}μs, RTT={RTT:F0}μs",
                    measuredOffset, rtt);
                return;
            }

            // Calculate time delta since last update (in seconds)
            double dt = (t4 - _lastUpdateTime) / 1_000_000.0;
            if (dt <= 0)
            {
                _logger?.LogWarning("Non-positive time delta: {Dt}s, skipping measurement", dt);
                return;
            }

            // ═══════════════════════════════════════════════════════════════════
            // KALMAN FILTER PREDICT STEP
            // ═══════════════════════════════════════════════════════════════════
            // State transition: offset += drift * dt
            // The drift rate stays the same (random walk model)
            double predictedOffset = _offset + _drift * dt;
            double predictedDrift = _drift;

            // Predict covariance: P = F * P * F' + Q
            // F = [1, dt; 0, 1] (state transition matrix)
            // Q = [q_offset, 0; 0, q_drift] * dt (process noise)
            double p00 = _offsetVariance + 2 * _covariance * dt + _driftVariance * dt * dt
                        + _processNoiseOffset * dt;
            double p01 = _covariance + _driftVariance * dt;
            double p11 = _driftVariance + _processNoiseDrift * dt;

            // ═══════════════════════════════════════════════════════════════════
            // KALMAN FILTER UPDATE STEP
            // ═══════════════════════════════════════════════════════════════════
            // We only measure the offset directly, H = [1, 0]

            // Adaptive measurement noise based on RTT
            // Higher RTT = more uncertain measurement
            double adaptiveMeasurementNoise = _measurementNoise + rtt * rtt / 4.0;

            // Innovation (measurement residual)
            double innovation = measuredOffset - predictedOffset;

            // Innovation covariance: S = H * P * H' + R = P[0,0] + R
            double innovationVariance = p00 + adaptiveMeasurementNoise;

            // Kalman gain: K = P * H' / S = [P[0,0], P[0,1]]' / S
            double k0 = p00 / innovationVariance;  // Gain for offset
            double k1 = p01 / innovationVariance;  // Gain for drift

            // Update state estimate
            _offset = predictedOffset + k0 * innovation;
            _drift = predictedDrift + k1 * innovation;

            // Update covariance: P = (I - K * H) * P
            _offsetVariance = (1 - k0) * p00;
            _covariance = (1 - k0) * p01;
            _driftVariance = p11 - k1 * p01;

            // Ensure covariance stays positive definite
            if (_offsetVariance < 0) _offsetVariance = 1;
            if (_driftVariance < 0) _driftVariance = 0.01;

            _lastUpdateTime = t4;
            _measurementCount++;

            // Log progress
            if (_measurementCount <= 10 || _measurementCount % 10 == 0)
            {
                _logger?.LogDebug(
                    "Time sync #{Count}: offset={Offset:F0}μs (±{Uncertainty:F0}), " +
                    "drift={Drift:F2}μs/s, RTT={RTT:F0}μs, innovation={Innovation:F0}μs",
                    _measurementCount,
                    _offset,
                    Math.Sqrt(_offsetVariance),
                    _drift,
                    rtt,
                    innovation);
            }
        }
    }

    /// <summary>
    /// Converts a client timestamp to server time.
    /// </summary>
    /// <param name="clientTime">Client time in microseconds.</param>
    /// <returns>Estimated server time in microseconds.</returns>
    public long ClientToServerTime(long clientTime)
    {
        lock (_lock)
        {
            // Account for drift since last update
            if (_lastUpdateTime > 0)
            {
                double elapsedSeconds = (clientTime - _lastUpdateTime) / 1_000_000.0;
                double currentOffset = _offset + _drift * elapsedSeconds;
                return clientTime + (long)currentOffset;
            }
            return clientTime + (long)_offset;
        }
    }

    /// <summary>
    /// Converts a server timestamp to client time.
    /// </summary>
    /// <param name="serverTime">Server time in microseconds.</param>
    /// <returns>Estimated client time in microseconds.</returns>
    public long ServerToClientTime(long serverTime)
    {
        lock (_lock)
        {
            // This is approximate since we'd need to solve for the exact time
            return serverTime - (long)_offset;
        }
    }

    /// <summary>
    /// Gets the current synchronization status for diagnostics.
    /// </summary>
    public ClockSyncStatus GetStatus()
    {
        lock (_lock)
        {
            return new ClockSyncStatus
            {
                OffsetMicroseconds = _offset,
                DriftMicrosecondsPerSecond = _drift,
                OffsetUncertaintyMicroseconds = Math.Sqrt(_offsetVariance),
                MeasurementCount = _measurementCount,
                IsConverged = IsConverged
            };
        }
    }
}

/// <summary>
/// Interface for clock synchronization implementations.
/// </summary>
public interface IClockSynchronizer
{
    /// <summary>
    /// Processes a time sync measurement using the NTP 4-timestamp method.
    /// </summary>
    void ProcessMeasurement(long t1, long t2, long t3, long t4);

    /// <summary>
    /// Converts client time to server time.
    /// </summary>
    long ClientToServerTime(long clientTime);

    /// <summary>
    /// Converts server time to client time.
    /// </summary>
    long ServerToClientTime(long serverTime);

    /// <summary>
    /// Whether the synchronizer has converged to a stable estimate.
    /// </summary>
    bool IsConverged { get; }

    /// <summary>
    /// Resets the synchronizer state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    ClockSyncStatus GetStatus();
}

/// <summary>
/// Status information about clock synchronization.
/// </summary>
public record ClockSyncStatus
{
    /// <summary>
    /// Estimated offset: server_time = client_time + offset.
    /// </summary>
    public double OffsetMicroseconds { get; init; }

    /// <summary>
    /// Estimated drift rate in microseconds per second.
    /// </summary>
    public double DriftMicrosecondsPerSecond { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of offset in microseconds.
    /// </summary>
    public double OffsetUncertaintyMicroseconds { get; init; }

    /// <summary>
    /// Number of measurements processed.
    /// </summary>
    public int MeasurementCount { get; init; }

    /// <summary>
    /// Whether synchronization has converged.
    /// </summary>
    public bool IsConverged { get; init; }

    /// <summary>
    /// Offset in milliseconds for display.
    /// </summary>
    public double OffsetMilliseconds => OffsetMicroseconds / 1000.0;
}
