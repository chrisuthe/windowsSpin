namespace SendSpin.SDK.Connection;

/// <summary>
/// Represents the current state of the WebSocket connection.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// Not connected to any server.
    /// </summary>
    Disconnected,

    /// <summary>
    /// Attempting to establish connection.
    /// </summary>
    Connecting,

    /// <summary>
    /// Connected but waiting for handshake completion.
    /// </summary>
    Handshaking,

    /// <summary>
    /// Fully connected and authenticated.
    /// </summary>
    Connected,

    /// <summary>
    /// Connection lost, attempting to reconnect.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// Gracefully disconnecting.
    /// </summary>
    Disconnecting
}

/// <summary>
/// Event args for connection state changes.
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionState OldState { get; init; }
    public ConnectionState NewState { get; init; }
    public string? Reason { get; init; }
    public Exception? Exception { get; init; }
}
