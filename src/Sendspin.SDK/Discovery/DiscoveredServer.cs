namespace Sendspin.SDK.Discovery;

/// <summary>
/// Represents a Sendspin server discovered via mDNS.
/// </summary>
public sealed class DiscoveredServer
{
    /// <summary>
    /// Unique server identifier from mDNS TXT record.
    /// </summary>
    required public string ServerId { get; init; }

    /// <summary>
    /// Human-readable server name.
    /// </summary>
    required public string Name { get; init; }

    /// <summary>
    /// Server hostname.
    /// </summary>
    required public string Host { get; init; }

    /// <summary>
    /// Server port number.
    /// </summary>
    required public int Port { get; init; }

    /// <summary>
    /// IP addresses for the server.
    /// </summary>
    required public IReadOnlyList<string> IpAddresses { get; init; }

    /// <summary>
    /// Protocol version advertised by the server.
    /// </summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Additional properties from TXT records.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Time when this server was first discovered.
    /// </summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Time when this server was last seen in discovery.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the WebSocket URI for connecting to this server.
    /// </summary>
    public Uri GetWebSocketUri()
    {
        // Prefer IP address over hostname for reliability
        var address = IpAddresses.FirstOrDefault() ?? Host;

        // Use path from TXT record if available, otherwise default to /sendspin
        var path = Properties.TryGetValue("path", out var p) ? p : "/sendspin";
        if (!path.StartsWith('/'))
            path = "/" + path;

        return new Uri($"ws://{address}:{Port}{path}");
    }

    public override string ToString() => $"{Name} ({Host}:{Port})";

    public override bool Equals(object? obj)
    {
        return obj is DiscoveredServer other && ServerId == other.ServerId;
    }

    public override int GetHashCode() => ServerId.GetHashCode();
}
