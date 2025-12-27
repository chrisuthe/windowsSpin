namespace Sendspin.SDK.Models;

/// <summary>
/// Information about a discovered Sendspin server.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>
    /// Unique server identifier.
    /// </summary>
    required public string ServerId { get; init; }

    /// <summary>
    /// Human-readable server name.
    /// </summary>
    required public string Name { get; init; }

    /// <summary>
    /// Server hostname or IP address.
    /// </summary>
    required public string Host { get; init; }

    /// <summary>
    /// Server port number.
    /// </summary>
    required public int Port { get; init; }

    /// <summary>
    /// Full WebSocket URI for connection.
    /// </summary>
    public string WebSocketUri => $"ws://{Host}:{Port}/sendspin";

    /// <summary>
    /// Protocol version supported by the server.
    /// </summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Whether the server was discovered via mDNS.
    /// </summary>
    public bool IsDiscovered { get; init; }

    /// <summary>
    /// Time when this server was last seen.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"{Name} ({Host}:{Port})";
}
