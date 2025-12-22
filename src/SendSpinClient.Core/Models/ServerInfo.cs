namespace SendSpinClient.Core.Models;

/// <summary>
/// Information about a discovered SendSpin server.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>
    /// Unique server identifier.
    /// </summary>
    public required string ServerId { get; init; }

    /// <summary>
    /// Human-readable server name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Server hostname or IP address.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Server port number.
    /// </summary>
    public required int Port { get; init; }

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
