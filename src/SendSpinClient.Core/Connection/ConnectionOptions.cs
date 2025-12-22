namespace SendSpinClient.Core.Connection;

/// <summary>
/// Configuration options for the SendSpin connection.
/// </summary>
public sealed class ConnectionOptions
{
    /// <summary>
    /// Maximum number of reconnection attempts before giving up.
    /// Set to -1 for infinite retries.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = -1;

    /// <summary>
    /// Initial delay between reconnection attempts in milliseconds.
    /// </summary>
    public int ReconnectDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay between reconnection attempts in milliseconds.
    /// </summary>
    public int MaxReconnectDelayMs { get; set; } = 30000;

    /// <summary>
    /// Multiplier for exponential backoff.
    /// </summary>
    public double ReconnectBackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Interval for sending keep-alive pings in milliseconds.
    /// Set to 0 to disable.
    /// </summary>
    public int KeepAliveIntervalMs { get; set; } = 30000;

    /// <summary>
    /// Buffer size for receiving WebSocket messages.
    /// </summary>
    public int ReceiveBufferSize { get; set; } = 64 * 1024; // 64KB

    /// <summary>
    /// Whether to automatically reconnect on connection loss.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;
}
