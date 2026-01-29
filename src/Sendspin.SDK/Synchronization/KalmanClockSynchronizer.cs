using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Synchronization;

/// <summary>
/// High-precision clock synchronizer using a 4D Kalman filter.
/// Tracks clock offset, drift rate, drift acceleration, and expected RTT
/// for accurate audio synchronization with network resilience.
///
/// The Kalman filter state vector is [offset, drift, accel, rtt]:
/// - offset: difference between server and client clocks (server_time = client_time + offset)
/// - drift: rate of change of offset (microseconds per second)
/// - accel: rate of change of drift (microseconds per second²) - for thermal adaptation
/// - rtt: expected round-trip time (microseconds) - for network-aware measurement weighting
///
/// This approach handles network jitter by statistically filtering measurements
/// while also tracking and compensating for clock drift and network changes.
/// </summary>
public sealed class KalmanClockSynchronizer : IClockSynchronizer
{
    private readonly ILogger<KalmanClockSynchronizer>? _logger;
    private readonly object _lock = new();

    // ═══════════════════════════════════════════════════════════════════════════
    // STATE INDICES
    // ═══════════════════════════════════════════════════════════════════════════
    private const int StateOffset = 0;
    private const int StateDrift = 1;
    private const int StateAccel = 2;
    private const int StateRtt = 3;
    private const int StateSize = 4;

    // ═══════════════════════════════════════════════════════════════════════════
    // KALMAN FILTER STATE
    // ═══════════════════════════════════════════════════════════════════════════
    // State vector: [offset, drift, accel, rtt]
    private readonly double[] _state = new double[StateSize];

    // Covariance matrix: 4×4 symmetric matrix
    // P[i,j] represents uncertainty correlation between state[i] and state[j]
    private readonly double[,] _P = new double[StateSize, StateSize];

    // Timing
    private long _lastUpdateTime;     // Last measurement time in microseconds
    private int _measurementCount;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION - PROCESS NOISE
    // ═══════════════════════════════════════════════════════════════════════════
    private readonly double _processNoiseOffset;   // How much offset can change per second (μs²/s)
    private readonly double _processNoiseDrift;    // How much drift rate can change per second (μs²/s³)
    private readonly double _processNoiseAccel;    // How much accel can change per second (μs²/s⁵)
    private readonly double _processNoiseRtt;      // How much expected RTT can vary (μs²/s)

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION - MEASUREMENT NOISE
    // ═══════════════════════════════════════════════════════════════════════════
    private readonly double _measurementNoise;     // Base measurement noise for offset (μs²)
    private readonly double _rttMeasurementNoise;  // Measurement noise for RTT observations (μs²)

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION - DECAY FACTORS
    // ═══════════════════════════════════════════════════════════════════════════
    private readonly double _accelDecay;           // Accel decay factor (0.90-0.95), decays toward 0
    private readonly double _rttDecay;             // RTT decay factor (0.98-0.99), slow adaptation

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION - FEATURE FLAGS
    // ═══════════════════════════════════════════════════════════════════════════
    private readonly bool _enableAccelTracking;    // Enable drift acceleration (4D feature)
    private readonly bool _enableRttTracking;      // Enable RTT state tracking (4D feature)

    // User-configurable playback delay
    private long _staticDelayMicroseconds;

    // ═══════════════════════════════════════════════════════════════════════════
    // ADAPTIVE FORGETTING
    // ═══════════════════════════════════════════════════════════════════════════
    private readonly double _forgetVarianceFactor; // forgetFactor² - covariance scaling factor
    private readonly double _adaptiveCutoff;       // Threshold multiplier for triggering offset-based forgetting
    private readonly double _rttChangeCutoff;      // Threshold multiplier for triggering RTT-based forgetting
    private readonly int _minSamplesForForgetting; // Don't adapt until this many samples collected
    private int _adaptiveForgettingTriggerCount;   // Diagnostic counter (offset-based)
    private int _networkChangeTriggerCount;        // Diagnostic counter (RTT-based)

    // ═══════════════════════════════════════════════════════════════════════════
    // CONVERGENCE THRESHOLDS
    // ═══════════════════════════════════════════════════════════════════════════
    private const int MinMeasurementsForConvergence = 5;
    private const int MinMeasurementsForPlayback = 2;  // Quick start: 2 measurements like JS/CLI players
    private const double MaxOffsetUncertaintyForConvergence = 1000.0; // 1ms uncertainty threshold
    private const double MaxDriftUncertaintyForReliable = 50.0; // μs/s
    private const double MaxRttUncertaintyForReliable = 500.0; // μs

    // Tracking for drift reliability transition (for diagnostics)
    private bool _driftReliableLogged;
    private bool _rttReliableLogged;

    /// <summary>
    /// Current estimated clock offset in microseconds.
    /// server_time = client_time + Offset
    /// </summary>
    public double Offset
    {
        get { lock (_lock) return _state[StateOffset]; }
    }

    /// <summary>
    /// Current estimated clock drift in microseconds per second.
    /// Positive means server clock is running faster than client.
    /// </summary>
    public double Drift
    {
        get { lock (_lock) return _state[StateDrift]; }
    }

    /// <summary>
    /// Current estimated drift acceleration in microseconds per second².
    /// Tracks rate of drift change for thermal/power adaptation.
    /// </summary>
    public double DriftAcceleration
    {
        get { lock (_lock) return _state[StateAccel]; }
    }

    /// <summary>
    /// Current expected round-trip time in microseconds.
    /// Used for network-aware measurement weighting.
    /// </summary>
    public double ExpectedRtt
    {
        get { lock (_lock) return _state[StateRtt]; }
    }

    /// <summary>
    /// Uncertainty (standard deviation) of the offset estimate in microseconds.
    /// </summary>
    public double OffsetUncertainty
    {
        get { lock (_lock) return Math.Sqrt(_P[StateOffset, StateOffset]); }
    }

    /// <summary>
    /// Uncertainty (standard deviation) of the drift estimate in μs/s.
    /// </summary>
    public double DriftUncertainty
    {
        get { lock (_lock) return Math.Sqrt(_P[StateDrift, StateDrift]); }
    }

    /// <summary>
    /// Uncertainty (standard deviation) of the expected RTT estimate in microseconds.
    /// </summary>
    public double RttUncertainty
    {
        get { lock (_lock) return Math.Sqrt(_P[StateRtt, StateRtt]); }
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
    /// Requires 5+ measurements and low offset uncertainty.
    /// </summary>
    public bool IsConverged
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForConvergence
                       && Math.Sqrt(_P[StateOffset, StateOffset]) < MaxOffsetUncertaintyForConvergence;
            }
        }
    }

    /// <summary>
    /// Whether the synchronizer has enough measurements for playback (at least 2).
    /// Unlike <see cref="IsConverged"/>, this doesn't require statistical convergence.
    /// The sync correction system handles any estimation errors during initial playback.
    /// </summary>
    /// <remarks>
    /// This matches the JS/CLI player behavior which starts after 2 measurements (~300-500ms)
    /// rather than waiting for full Kalman filter convergence (5+ measurements, ~1-5 seconds).
    /// </remarks>
    public bool HasMinimalSync
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForPlayback;
            }
        }
    }

    /// <summary>
    /// Whether the drift estimate is reliable enough to use for time conversions.
    /// Drift estimation requires longer time periods to be accurate, so we don't
    /// apply drift compensation until the filter is confident in the estimate.
    /// </summary>
    public bool IsDriftReliable
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForConvergence
                       && Math.Sqrt(_P[StateDrift, StateDrift]) < MaxDriftUncertaintyForReliable;
            }
        }
    }

    /// <summary>
    /// Whether the RTT estimate is reliable enough for network-aware weighting.
    /// </summary>
    public bool IsRttReliable
    {
        get
        {
            lock (_lock)
            {
                return _enableRttTracking
                       && _measurementCount >= MinMeasurementsForConvergence
                       && Math.Sqrt(_P[StateRtt, StateRtt]) < MaxRttUncertaintyForReliable;
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
    /// <param name="forgetFactor">Adaptive forgetting factor (default: 1.0 = disabled, 1.001 = recommended).
    /// When prediction error exceeds the threshold, covariance is scaled by forgetFactor² to
    /// "forget" old measurements faster and recover from network disruptions.</param>
    /// <param name="adaptiveCutoff">Threshold multiplier for triggering adaptive forgetting (default: 0.75).
    /// Forgetting triggers when prediction error exceeds adaptiveCutoff × sqrt(predicted variance).</param>
    /// <param name="minSamplesForForgetting">Minimum measurements before adaptive forgetting activates (default: 100).
    /// Prevents forgetting during initial convergence phase.</param>
    /// <param name="enableAccelTracking">Enable drift acceleration tracking (default: false).</param>
    /// <param name="enableRttTracking">Enable RTT state tracking (default: false).</param>
    /// <param name="processNoiseAccel">Process noise for acceleration (default: 0.1 μs²/s⁵).</param>
    /// <param name="processNoiseRtt">Process noise for RTT (default: 1000 μs²/s).</param>
    /// <param name="accelDecay">Decay factor for acceleration (default: 0.92).</param>
    /// <param name="rttDecay">Decay factor for RTT (default: 0.98).</param>
    /// <param name="rttMeasurementNoise">Measurement noise for RTT observations (default: 1000 μs²).</param>
    /// <param name="rttChangeCutoff">Threshold multiplier for RTT-based network change detection (default: 2.0).
    /// Network change triggers when |measured_rtt - expected_rtt| > rttChangeCutoff × sqrt(RTT variance).</param>
    public KalmanClockSynchronizer(
        ILogger<KalmanClockSynchronizer>? logger = null,
        double processNoiseOffset = 100.0,
        double processNoiseDrift = 1.0,
        double measurementNoise = 10000.0,
        double forgetFactor = 1.0,
        double adaptiveCutoff = 0.75,
        int minSamplesForForgetting = 100,
        bool enableAccelTracking = false,
        bool enableRttTracking = false,
        double processNoiseAccel = 0.1,
        double processNoiseRtt = 1000.0,
        double accelDecay = 0.92,
        double rttDecay = 0.98,
        double rttMeasurementNoise = 1000.0,
        double rttChangeCutoff = 2.0)
    {
        _logger = logger;
        _processNoiseOffset = processNoiseOffset;
        _processNoiseDrift = processNoiseDrift;
        _processNoiseAccel = processNoiseAccel;
        _processNoiseRtt = processNoiseRtt;
        _measurementNoise = measurementNoise;
        _rttMeasurementNoise = rttMeasurementNoise;
        _forgetVarianceFactor = forgetFactor * forgetFactor; // Square for covariance scaling
        _adaptiveCutoff = adaptiveCutoff;
        _rttChangeCutoff = rttChangeCutoff;
        _minSamplesForForgetting = minSamplesForForgetting;
        _enableAccelTracking = enableAccelTracking;
        _enableRttTracking = enableRttTracking;
        _accelDecay = accelDecay;
        _rttDecay = rttDecay;

        Reset();
    }

    /// <summary>
    /// Resets the synchronizer to initial state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            // Reset state vector
            _state[StateOffset] = 0;
            _state[StateDrift] = 0;
            _state[StateAccel] = 0;
            _state[StateRtt] = 0;

            // Reset covariance matrix - high initial uncertainty
            // Clear all elements first
            Array.Clear(_P);

            // Set diagonal (variances)
            _P[StateOffset, StateOffset] = 1e12;  // 1 second offset uncertainty
            _P[StateDrift, StateDrift] = 1e6;     // 1000 μs/s drift uncertainty
            _P[StateAccel, StateAccel] = 1e4;     // 100 μs/s² accel uncertainty
            _P[StateRtt, StateRtt] = 1e8;         // 10 second RTT uncertainty (will converge quickly)

            // Off-diagonal elements start at zero (no assumed correlation)

            _lastUpdateTime = 0;
            _measurementCount = 0;
            _driftReliableLogged = false;
            _rttReliableLogged = false;
            _adaptiveForgettingTriggerCount = 0;
            _networkChangeTriggerCount = 0;
        }

        _logger?.LogDebug("Clock synchronizer reset (4D Kalman filter, accel={Accel}, rtt={Rtt})",
            _enableAccelTracking, _enableRttTracking);
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
        ProcessMeasurementWithBurstStats(t1, t2, t3, t4, burstStats: null);
    }

    /// <summary>
    /// Processes a complete time exchange measurement with optional burst statistics.
    /// </summary>
    /// <param name="t1">Client transmit time (T1) in microseconds.</param>
    /// <param name="t2">Server receive time (T2) in microseconds.</param>
    /// <param name="t3">Server transmit time (T3) in microseconds.</param>
    /// <param name="t4">Client receive time (T4) in microseconds.</param>
    /// <param name="burstStats">Optional burst statistics for enhanced noise estimation.</param>
    public void ProcessMeasurementWithBurstStats(long t1, long t2, long t3, long t4, BurstStatistics? burstStats)
    {
        // Calculate offset using NTP formula
        // offset = ((T2 - T1) + (T3 - T4)) / 2
        double measuredOffset = ((t2 - t1) + (t3 - t4)) / 2.0;

        // Round-trip time for quality assessment
        // RTT = (T4 - T1) - (T3 - T2)
        double measuredRtt = (t4 - t1) - (t3 - t2);

        lock (_lock)
        {
            // First measurement: initialize state
            if (_measurementCount == 0)
            {
                _state[StateOffset] = measuredOffset;
                _state[StateRtt] = measuredRtt;
                _lastUpdateTime = t4;
                _measurementCount = 1;

                _logger?.LogDebug(
                    "Initial time sync: offset={Offset:F0}μs, RTT={RTT:F0}μs",
                    measuredOffset, measuredRtt);
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
            PredictState(dt);
            PredictCovariance(dt);

            // ═══════════════════════════════════════════════════════════════════
            // ADAPTIVE FORGETTING (offset-based)
            // ═══════════════════════════════════════════════════════════════════
            double predictedOffset = _state[StateOffset];
            if (_measurementCount >= _minSamplesForForgetting && _forgetVarianceFactor > 1.0)
            {
                double predictionError = Math.Abs(measuredOffset - predictedOffset);
                double threshold = _adaptiveCutoff * Math.Sqrt(_P[StateOffset, StateOffset]);

                if (predictionError > threshold)
                {
                    ScaleCovariance(_forgetVarianceFactor);
                    _adaptiveForgettingTriggerCount++;

                    _logger?.LogWarning(
                        "⚡ Adaptive forgetting triggered (#{Count}): prediction error {Error:F0}μs > " +
                        "threshold {Threshold:F0}μs. Scaling covariance for faster recovery.",
                        _adaptiveForgettingTriggerCount,
                        predictionError,
                        threshold);
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // KALMAN FILTER UPDATE STEP - OFFSET MEASUREMENT
            // ═══════════════════════════════════════════════════════════════════
            double adaptiveMeasurementNoise = CalculateOffsetMeasurementNoise(measuredRtt, burstStats);
            UpdateWithOffsetMeasurement(measuredOffset, adaptiveMeasurementNoise);

            // ═══════════════════════════════════════════════════════════════════
            // KALMAN FILTER UPDATE STEP - RTT MEASUREMENT (if enabled)
            // ═══════════════════════════════════════════════════════════════════
            if (_enableRttTracking)
            {
                UpdateWithRttMeasurement(measuredRtt, _rttMeasurementNoise);

                // ═══════════════════════════════════════════════════════════════
                // ADAPTIVE FORGETTING (RTT-based network change detection)
                // ═══════════════════════════════════════════════════════════════
                // Check for significant RTT changes that indicate network topology changes
                // (e.g., WiFi → Ethernet, path switching, congestion changes)
                if (_measurementCount >= _minSamplesForForgetting && _forgetVarianceFactor > 1.0)
                {
                    // Compare measured RTT to expected RTT (state was just updated, so use innovation from before update)
                    double rttInnovation = Math.Abs(measuredRtt - _state[StateRtt]);
                    double rttStdDev = Math.Sqrt(_P[StateRtt, StateRtt]);
                    double rttThreshold = _rttChangeCutoff * rttStdDev;

                    // Only trigger if RTT uncertainty is reasonable (filter has learned the baseline)
                    if (rttStdDev < MaxRttUncertaintyForReliable && rttInnovation > rttThreshold)
                    {
                        // Network changed - forget offset/drift history but preserve RTT learning
                        ScaleOffsetDriftCovariance(_forgetVarianceFactor);
                        _networkChangeTriggerCount++;

                        _logger?.LogWarning(
                            "🌐 Network change detected (#{Count}): RTT {Expected:F0}μs → {Measured:F0}μs " +
                            "(deviation {Deviation:F0}μs > threshold {Threshold:F0}μs). " +
                            "Scaling offset/drift covariance for faster recovery.",
                            _networkChangeTriggerCount,
                            _state[StateRtt],
                            measuredRtt,
                            rttInnovation,
                            rttThreshold);
                    }
                }
            }

            // Ensure covariance stays positive definite
            EnsurePositiveDefinite();

            _lastUpdateTime = t4;
            _measurementCount++;

            // Log progress
            LogProgress(measuredRtt);
        }
    }

    /// <summary>
    /// Predicts state forward by dt seconds using the kinematic model.
    /// </summary>
    private void PredictState(double dt)
    {
        double dt2 = dt * dt;

        // Kinematic prediction for offset/drift/accel
        if (_enableAccelTracking)
        {
            // offset += drift × dt + 0.5 × accel × dt²
            _state[StateOffset] += _state[StateDrift] * dt + _state[StateAccel] * dt2 / 2.0;
            // drift += accel × dt
            _state[StateDrift] += _state[StateAccel] * dt;
            // accel decays toward zero
            _state[StateAccel] *= _accelDecay;
        }
        else
        {
            // 2D behavior: offset += drift × dt
            _state[StateOffset] += _state[StateDrift] * dt;
        }

        // RTT decays toward long-term mean (implemented as decay toward current value)
        if (_enableRttTracking)
        {
            _state[StateRtt] *= _rttDecay;
        }
    }

    /// <summary>
    /// Predicts covariance matrix forward: P = F × P × F' + Q
    /// </summary>
    private void PredictCovariance(double dt)
    {
        double a = dt;
        double a2 = dt * dt;
        double b = a2 / 2.0;
        double ab = a * b;
        double b2 = b * b;
        double g = _accelDecay;
        double g2 = g * g;
        double r = _rttDecay;
        double r2 = r * r;

        // Store old covariance values (upper triangle only, matrix is symmetric)
        double p00 = _P[StateOffset, StateOffset];
        double p01 = _P[StateOffset, StateDrift];
        double p02 = _P[StateOffset, StateAccel];
        double p03 = _P[StateOffset, StateRtt];
        double p11 = _P[StateDrift, StateDrift];
        double p12 = _P[StateDrift, StateAccel];
        double p13 = _P[StateDrift, StateRtt];
        double p22 = _P[StateAccel, StateAccel];
        double p23 = _P[StateAccel, StateRtt];
        double p33 = _P[StateRtt, StateRtt];

        if (_enableAccelTracking)
        {
            // Full 4D kinematic covariance prediction
            // P[0,0] = p00 + 2a·p01 + 2b·p02 + a²·p11 + 2ab·p12 + b²·p22 + Q[0,0]
            _P[StateOffset, StateOffset] = p00 + 2 * a * p01 + 2 * b * p02 + a2 * p11 + 2 * ab * p12 + b2 * p22 + _processNoiseOffset * dt;
            _P[StateOffset, StateDrift] = p01 + a * p11 + b * p12 + a * p02 + a2 * p12 + ab * p22;
            _P[StateOffset, StateAccel] = g * (p02 + a * p12 + b * p22);
            _P[StateDrift, StateDrift] = p11 + 2 * a * p12 + a2 * p22 + _processNoiseDrift * dt;
            _P[StateDrift, StateAccel] = g * (p12 + a * p22);
            _P[StateAccel, StateAccel] = g2 * p22 + _processNoiseAccel * dt;
        }
        else
        {
            // 2D covariance prediction (matches original behavior)
            // P = F × P × F' + Q where F = [1 dt; 0 1]
            _P[StateOffset, StateOffset] = p00 + 2 * a * p01 + a2 * p11 + _processNoiseOffset * dt;
            _P[StateOffset, StateDrift] = p01 + a * p11;
            _P[StateDrift, StateDrift] = p11 + _processNoiseDrift * dt;

            // Keep accel covariance stable when disabled
            _P[StateOffset, StateAccel] = 0;
            _P[StateDrift, StateAccel] = 0;
            _P[StateAccel, StateAccel] = 1e4; // Fixed high uncertainty
        }

        // RTT is independent of offset/drift/accel in state transition
        if (_enableRttTracking)
        {
            _P[StateOffset, StateRtt] = r * p03;
            _P[StateDrift, StateRtt] = r * p13;
            _P[StateAccel, StateRtt] = r * g * p23;
            _P[StateRtt, StateRtt] = r2 * p33 + _processNoiseRtt * dt;
        }
        else
        {
            _P[StateOffset, StateRtt] = 0;
            _P[StateDrift, StateRtt] = 0;
            _P[StateAccel, StateRtt] = 0;
            _P[StateRtt, StateRtt] = 1e8; // Fixed high uncertainty
        }

        // Copy to lower triangle (symmetric matrix)
        _P[StateDrift, StateOffset] = _P[StateOffset, StateDrift];
        _P[StateAccel, StateOffset] = _P[StateOffset, StateAccel];
        _P[StateAccel, StateDrift] = _P[StateDrift, StateAccel];
        _P[StateRtt, StateOffset] = _P[StateOffset, StateRtt];
        _P[StateRtt, StateDrift] = _P[StateDrift, StateRtt];
        _P[StateRtt, StateAccel] = _P[StateAccel, StateRtt];
    }

    /// <summary>
    /// Calculates adaptive measurement noise for offset based on RTT and burst statistics.
    /// </summary>
    private double CalculateOffsetMeasurementNoise(double measuredRtt, BurstStatistics? burstStats)
    {
        double noise = _measurementNoise;

        if (_enableRttTracking && _measurementCount > 0)
        {
            // 4D approach: penalize deviation from EXPECTED RTT
            double rttDeviation = measuredRtt - _state[StateRtt];
            noise += rttDeviation * rttDeviation / 4.0;
        }
        else
        {
            // 2D approach: penalize absolute RTT (original behavior)
            noise += measuredRtt * measuredRtt / 4.0;
        }

        // Enhanced noise using burst statistics (if available)
        if (burstStats != null)
        {
            // Additional penalty for high burst RTT variance
            if (burstStats.RttVariance > 0)
            {
                double expectedRttVariance = _P[StateRtt, StateRtt];
                if (expectedRttVariance > 0 && burstStats.RttVariance > expectedRttVariance * 2)
                {
                    noise += burstStats.RttVariance / 4.0;
                }
            }

            // Cross-validation: if offset spread is unexpectedly high, penalize heavily
            if (burstStats.OffsetSpread > 0)
            {
                double expectedOffsetSpread = 2 * Math.Sqrt(_P[StateOffset, StateOffset]);
                if (burstStats.OffsetSpread > 5 * expectedOffsetSpread && expectedOffsetSpread > 0)
                {
                    noise *= 10;  // Heavy penalty for inconsistent burst
                    _logger?.LogWarning(
                        "Inconsistent burst detected: offset spread={Spread:F0}μs >> expected={Expected:F0}μs. Penalizing measurement.",
                        burstStats.OffsetSpread, expectedOffsetSpread);
                }
            }
        }

        return noise;
    }

    /// <summary>
    /// Updates state and covariance with an offset measurement.
    /// H = [1, 0, 0, 0] (we observe offset directly)
    /// </summary>
    private void UpdateWithOffsetMeasurement(double measuredOffset, double measurementNoise)
    {
        double innovation = measuredOffset - _state[StateOffset];
        double S = _P[StateOffset, StateOffset] + measurementNoise;

        // Kalman gain: K = P × H' / S = P[*, 0] / S
        double k0 = _P[StateOffset, StateOffset] / S;
        double k1 = _P[StateDrift, StateOffset] / S;
        double k2 = _P[StateAccel, StateOffset] / S;
        double k3 = _P[StateRtt, StateOffset] / S;

        // State update: x = x + K × innovation
        _state[StateOffset] += k0 * innovation;
        _state[StateDrift] += k1 * innovation;
        if (_enableAccelTracking)
        {
            _state[StateAccel] += k2 * innovation;
        }
        if (_enableRttTracking)
        {
            _state[StateRtt] += k3 * innovation;
        }

        // Covariance update: P = P - K × P[0, *]
        // Store row 0 before modification
        double p0_0 = _P[StateOffset, StateOffset];
        double p0_1 = _P[StateOffset, StateDrift];
        double p0_2 = _P[StateOffset, StateAccel];
        double p0_3 = _P[StateOffset, StateRtt];

        _P[StateOffset, StateOffset] -= k0 * p0_0;
        _P[StateOffset, StateDrift] -= k0 * p0_1;
        _P[StateOffset, StateAccel] -= k0 * p0_2;
        _P[StateOffset, StateRtt] -= k0 * p0_3;
        _P[StateDrift, StateDrift] -= k1 * p0_1;
        _P[StateDrift, StateAccel] -= k1 * p0_2;
        _P[StateDrift, StateRtt] -= k1 * p0_3;
        _P[StateAccel, StateAccel] -= k2 * p0_2;
        _P[StateAccel, StateRtt] -= k2 * p0_3;
        _P[StateRtt, StateRtt] -= k3 * p0_3;

        // Symmetric copy
        _P[StateDrift, StateOffset] = _P[StateOffset, StateDrift];
        _P[StateAccel, StateOffset] = _P[StateOffset, StateAccel];
        _P[StateAccel, StateDrift] = _P[StateDrift, StateAccel];
        _P[StateRtt, StateOffset] = _P[StateOffset, StateRtt];
        _P[StateRtt, StateDrift] = _P[StateDrift, StateRtt];
        _P[StateRtt, StateAccel] = _P[StateAccel, StateRtt];
    }

    /// <summary>
    /// Updates state and covariance with an RTT measurement.
    /// H = [0, 0, 0, 1] (we observe RTT directly)
    /// </summary>
    private void UpdateWithRttMeasurement(double measuredRtt, double rttNoise)
    {
        double innovation = measuredRtt - _state[StateRtt];
        double S = _P[StateRtt, StateRtt] + rttNoise;

        // Kalman gain: K = P × H' / S = P[*, 3] / S
        double k0 = _P[StateOffset, StateRtt] / S;
        double k1 = _P[StateDrift, StateRtt] / S;
        double k2 = _P[StateAccel, StateRtt] / S;
        double k3 = _P[StateRtt, StateRtt] / S;

        // State update: x = x + K × innovation
        _state[StateOffset] += k0 * innovation;
        _state[StateDrift] += k1 * innovation;
        _state[StateAccel] += k2 * innovation;
        _state[StateRtt] += k3 * innovation;

        // Covariance update: P = P - K × P[3, *]
        double p3_0 = _P[StateRtt, StateOffset];
        double p3_1 = _P[StateRtt, StateDrift];
        double p3_2 = _P[StateRtt, StateAccel];
        double p3_3 = _P[StateRtt, StateRtt];

        _P[StateOffset, StateOffset] -= k0 * p3_0;
        _P[StateOffset, StateDrift] -= k0 * p3_1;
        _P[StateOffset, StateAccel] -= k0 * p3_2;
        _P[StateOffset, StateRtt] -= k0 * p3_3;
        _P[StateDrift, StateDrift] -= k1 * p3_1;
        _P[StateDrift, StateAccel] -= k1 * p3_2;
        _P[StateDrift, StateRtt] -= k1 * p3_3;
        _P[StateAccel, StateAccel] -= k2 * p3_2;
        _P[StateAccel, StateRtt] -= k2 * p3_3;
        _P[StateRtt, StateRtt] -= k3 * p3_3;

        // Symmetric copy
        _P[StateDrift, StateOffset] = _P[StateOffset, StateDrift];
        _P[StateAccel, StateOffset] = _P[StateOffset, StateAccel];
        _P[StateAccel, StateDrift] = _P[StateDrift, StateAccel];
        _P[StateRtt, StateOffset] = _P[StateOffset, StateRtt];
        _P[StateRtt, StateDrift] = _P[StateDrift, StateRtt];
        _P[StateRtt, StateAccel] = _P[StateAccel, StateRtt];
    }

    /// <summary>
    /// Scales the covariance matrix by a factor (for adaptive forgetting).
    /// </summary>
    private void ScaleCovariance(double factor)
    {
        for (int i = 0; i < StateSize; i++)
        {
            for (int j = 0; j < StateSize; j++)
            {
                _P[i, j] *= factor;
            }
        }
    }

    /// <summary>
    /// Scales only the offset/drift portion of the covariance matrix (for RTT-based network change detection).
    /// Preserves RTT covariance so the filter can learn the new network baseline.
    /// </summary>
    private void ScaleOffsetDriftCovariance(double factor)
    {
        // Scale offset variance and covariances
        _P[StateOffset, StateOffset] *= factor;
        _P[StateOffset, StateDrift] *= factor;
        _P[StateDrift, StateOffset] *= factor;
        _P[StateDrift, StateDrift] *= factor;

        // Also scale accel if it's being tracked (accel correlates with drift)
        if (_enableAccelTracking)
        {
            _P[StateOffset, StateAccel] *= factor;
            _P[StateAccel, StateOffset] *= factor;
            _P[StateDrift, StateAccel] *= factor;
            _P[StateAccel, StateDrift] *= factor;
            _P[StateAccel, StateAccel] *= factor;
        }

        // Note: RTT covariance (_P[StateRtt, *]) is NOT scaled
        // We want to preserve RTT learning so the filter adapts to the new network baseline
    }

    /// <summary>
    /// Ensures the covariance matrix diagonal stays positive.
    /// </summary>
    private void EnsurePositiveDefinite()
    {
        // Minimum variances to prevent filter collapse
        if (_P[StateOffset, StateOffset] < 1.0) _P[StateOffset, StateOffset] = 1.0;
        if (_P[StateDrift, StateDrift] < 0.01) _P[StateDrift, StateDrift] = 0.01;
        if (_P[StateAccel, StateAccel] < 0.001) _P[StateAccel, StateAccel] = 0.001;
        if (_P[StateRtt, StateRtt] < 100.0) _P[StateRtt, StateRtt] = 100.0;
    }

    /// <summary>
    /// Logs filter progress at key intervals.
    /// </summary>
    private void LogProgress(double measuredRtt)
    {
        if (_measurementCount <= 10 || _measurementCount % 10 == 0)
        {
            if (_enableAccelTracking || _enableRttTracking)
            {
                _logger?.LogDebug(
                    "Time sync #{Count}: offset={Offset:F0}μs (±{OffsetUnc:F0}), " +
                    "drift={Drift:F2}μs/s (±{DriftUnc:F1}), accel={Accel:F3}μs/s², expRtt={ExpRtt:F0}μs, RTT={RTT:F0}μs",
                    _measurementCount,
                    _state[StateOffset],
                    Math.Sqrt(_P[StateOffset, StateOffset]),
                    _state[StateDrift],
                    Math.Sqrt(_P[StateDrift, StateDrift]),
                    _state[StateAccel],
                    _state[StateRtt],
                    measuredRtt);
            }
            else
            {
                _logger?.LogDebug(
                    "Time sync #{Count}: offset={Offset:F0}μs (±{Uncertainty:F0}), " +
                    "drift={Drift:F2}μs/s (±{DriftUncertainty:F1}), RTT={RTT:F0}μs",
                    _measurementCount,
                    _state[StateOffset],
                    Math.Sqrt(_P[StateOffset, StateOffset]),
                    _state[StateDrift],
                    Math.Sqrt(_P[StateDrift, StateDrift]),
                    measuredRtt);
            }
        }

        // Log when drift becomes reliable for the first time
        bool driftNowReliable = _measurementCount >= MinMeasurementsForConvergence
                               && Math.Sqrt(_P[StateDrift, StateDrift]) < MaxDriftUncertaintyForReliable;
        if (driftNowReliable && !_driftReliableLogged)
        {
            _driftReliableLogged = true;
            _logger?.LogInformation(
                "Drift is now reliable: drift={Drift:F2}μs/s (±{Uncertainty:F1}μs/s), " +
                "offset={Offset:F0}μs, measurements={Count}. " +
                "Future timestamps will include drift compensation.",
                _state[StateDrift],
                Math.Sqrt(_P[StateDrift, StateDrift]),
                _state[StateOffset],
                _measurementCount);
        }

        // Log when RTT becomes reliable (if enabled)
        if (_enableRttTracking)
        {
            bool rttNowReliable = _measurementCount >= MinMeasurementsForConvergence
                                  && Math.Sqrt(_P[StateRtt, StateRtt]) < MaxRttUncertaintyForReliable;
            if (rttNowReliable && !_rttReliableLogged)
            {
                _rttReliableLogged = true;
                _logger?.LogInformation(
                    "RTT tracking is now reliable: expectedRtt={Rtt:F0}μs (±{Uncertainty:F0}μs), " +
                    "measurements={Count}. Network-aware measurement weighting active.",
                    _state[StateRtt],
                    Math.Sqrt(_P[StateRtt, StateRtt]),
                    _measurementCount);
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
            if (_lastUpdateTime > 0)
            {
                double elapsedSeconds = (clientTime - _lastUpdateTime) / 1_000_000.0;

                // Only apply drift compensation when we're confident in the estimate
                bool driftReliable = _measurementCount >= MinMeasurementsForConvergence
                                    && Math.Sqrt(_P[StateDrift, StateDrift]) < MaxDriftUncertaintyForReliable;

                double currentOffset;
                if (driftReliable)
                {
                    if (_enableAccelTracking)
                    {
                        // Full kinematic: offset + drift×t + 0.5×accel×t²
                        currentOffset = _state[StateOffset]
                            + _state[StateDrift] * elapsedSeconds
                            + _state[StateAccel] * elapsedSeconds * elapsedSeconds / 2.0;
                    }
                    else
                    {
                        currentOffset = _state[StateOffset] + _state[StateDrift] * elapsedSeconds;
                    }
                }
                else
                {
                    currentOffset = _state[StateOffset];
                }

                return clientTime + (long)currentOffset;
            }
            return clientTime + (long)_state[StateOffset];
        }
    }

    /// <summary>
    /// Converts a server timestamp to client time.
    /// </summary>
    /// <param name="serverTime">Server time in microseconds.</param>
    /// <returns>Estimated client time in microseconds.</returns>
    /// <remarks>
    /// <para>
    /// Includes static delay: positive delay means play LATER (adds to client time).
    /// This allows manual tuning to sync with other players.
    /// </para>
    /// <para>
    /// Applies drift compensation when drift estimate is reliable, mirroring
    /// the behavior of ClientToServerTime. This is critical for accurate audio
    /// timestamp conversion during long playback sessions.
    /// </para>
    /// </remarks>
    public long ServerToClientTime(long serverTime)
    {
        lock (_lock)
        {
            if (_lastUpdateTime > 0)
            {
                // We need elapsed time since last update to extrapolate drift.
                // Use the approximate client time to calculate elapsed seconds.
                long approxClientTime = serverTime - (long)_state[StateOffset];
                double elapsedSeconds = (approxClientTime - _lastUpdateTime) / 1_000_000.0;

                // Only apply drift when we're confident in the estimate
                bool driftReliable = _measurementCount >= MinMeasurementsForConvergence
                                    && Math.Sqrt(_P[StateDrift, StateDrift]) < MaxDriftUncertaintyForReliable;

                double currentOffset;
                if (driftReliable)
                {
                    if (_enableAccelTracking)
                    {
                        // Full kinematic
                        currentOffset = _state[StateOffset]
                            + _state[StateDrift] * elapsedSeconds
                            + _state[StateAccel] * elapsedSeconds * elapsedSeconds / 2.0;
                    }
                    else
                    {
                        currentOffset = _state[StateOffset] + _state[StateDrift] * elapsedSeconds;
                    }
                }
                else
                {
                    currentOffset = _state[StateOffset];
                }

                // Static delay is added (positive = play later, per user preference)
                return serverTime - (long)currentOffset + _staticDelayMicroseconds;
            }

            return serverTime - (long)_state[StateOffset] + _staticDelayMicroseconds;
        }
    }

    /// <summary>
    /// Gets or sets the static delay in milliseconds.
    /// Positive values delay playback (play later), negative values advance it (play earlier).
    /// </summary>
    public double StaticDelayMs
    {
        get => _staticDelayMicroseconds / 1000.0;
        set => _staticDelayMicroseconds = (long)(value * 1000);
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
                OffsetMicroseconds = _state[StateOffset],
                DriftMicrosecondsPerSecond = _state[StateDrift],
                DriftAccelerationMicrosecondsPerSecondSquared = _state[StateAccel],
                ExpectedRttMicroseconds = _state[StateRtt],
                OffsetUncertaintyMicroseconds = Math.Sqrt(_P[StateOffset, StateOffset]),
                DriftUncertaintyMicrosecondsPerSecond = Math.Sqrt(_P[StateDrift, StateDrift]),
                RttUncertaintyMicroseconds = Math.Sqrt(_P[StateRtt, StateRtt]),
                MeasurementCount = _measurementCount,
                IsConverged = IsConverged,
                IsDriftReliable = IsDriftReliable,
                IsRttReliable = IsRttReliable,
                AdaptiveForgettingTriggerCount = _adaptiveForgettingTriggerCount,
                NetworkChangeTriggerCount = _networkChangeTriggerCount
            };
        }
    }
}

/// <summary>
/// Statistics from a burst of time sync measurements.
/// Used to enhance measurement quality assessment.
/// </summary>
public record BurstStatistics
{
    /// <summary>
    /// Minimum RTT in the burst (μs).
    /// </summary>
    public double MinRtt { get; init; }

    /// <summary>
    /// Maximum RTT in the burst (μs).
    /// </summary>
    public double MaxRtt { get; init; }

    /// <summary>
    /// Mean RTT of all measurements in the burst (μs).
    /// </summary>
    public double MeanRtt { get; init; }

    /// <summary>
    /// Variance of RTT within the burst (μs²).
    /// High variance indicates unstable network conditions.
    /// </summary>
    public double RttVariance { get; init; }

    /// <summary>
    /// Spread of offset measurements (max - min) in the burst (μs).
    /// High spread indicates inconsistent measurements.
    /// </summary>
    public double OffsetSpread { get; init; }

    /// <summary>
    /// Number of measurements in the burst.
    /// </summary>
    public int SampleCount { get; init; }
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
    /// Processes a time sync measurement with optional burst statistics for enhanced quality estimation.
    /// </summary>
    /// <param name="t1">Client transmit time (T1) in microseconds.</param>
    /// <param name="t2">Server receive time (T2) in microseconds.</param>
    /// <param name="t3">Server transmit time (T3) in microseconds.</param>
    /// <param name="t4">Client receive time (T4) in microseconds.</param>
    /// <param name="burstStats">Optional statistics from the burst for enhanced noise estimation.</param>
    void ProcessMeasurementWithBurstStats(long t1, long t2, long t3, long t4, BurstStatistics? burstStats);

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
    /// Requires 5+ measurements and low offset uncertainty.
    /// </summary>
    bool IsConverged { get; }

    /// <summary>
    /// Whether the synchronizer has enough measurements for playback (at least 2).
    /// Unlike <see cref="IsConverged"/>, this doesn't require statistical convergence.
    /// </summary>
    bool HasMinimalSync { get; }

    /// <summary>
    /// Resets the synchronizer state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    ClockSyncStatus GetStatus();

    /// <summary>
    /// Gets or sets the static delay in milliseconds.
    /// Positive values delay playback (play later), negative values advance it (play earlier).
    /// </summary>
    double StaticDelayMs { get; set; }
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
    /// Estimated drift acceleration in microseconds per second².
    /// Tracks rate of drift change for thermal/power adaptation.
    /// </summary>
    public double DriftAccelerationMicrosecondsPerSecondSquared { get; init; }

    /// <summary>
    /// Expected round-trip time in microseconds.
    /// Used for network-aware measurement weighting.
    /// </summary>
    public double ExpectedRttMicroseconds { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of offset in microseconds.
    /// </summary>
    public double OffsetUncertaintyMicroseconds { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of drift in microseconds per second.
    /// </summary>
    public double DriftUncertaintyMicrosecondsPerSecond { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of expected RTT in microseconds.
    /// </summary>
    public double RttUncertaintyMicroseconds { get; init; }

    /// <summary>
    /// Number of measurements processed.
    /// </summary>
    public int MeasurementCount { get; init; }

    /// <summary>
    /// Whether synchronization has converged.
    /// </summary>
    public bool IsConverged { get; init; }

    /// <summary>
    /// Whether drift estimate is reliable enough for compensation.
    /// </summary>
    public bool IsDriftReliable { get; init; }

    /// <summary>
    /// Whether RTT estimate is reliable enough for network-aware weighting.
    /// </summary>
    public bool IsRttReliable { get; init; }

    /// <summary>
    /// Number of times adaptive forgetting was triggered due to large prediction errors.
    /// This indicates recovery from network disruptions or clock adjustments.
    /// </summary>
    public int AdaptiveForgettingTriggerCount { get; init; }

    /// <summary>
    /// Number of times RTT-based network change detection was triggered.
    /// This indicates the filter detected significant RTT shifts suggesting network topology changes.
    /// </summary>
    public int NetworkChangeTriggerCount { get; init; }

    /// <summary>
    /// Offset in milliseconds for display.
    /// </summary>
    public double OffsetMilliseconds => OffsetMicroseconds / 1000.0;
}
