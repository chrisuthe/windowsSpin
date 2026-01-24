namespace Sendspin.SDK.Client;

/// <summary>
/// Determines how the client establishes connections with Sendspin servers.
/// </summary>
public enum ConnectionMode
{
    /// <summary>
    /// Both discover servers and advertise as a player (default).
    /// Client-initiated connections take priority over server-initiated ones.
    /// </summary>
    Auto,

    /// <summary>
    /// Only advertise via mDNS and wait for servers to connect.
    /// Equivalent to the Python CLI's daemon mode.
    /// </summary>
    AdvertiseOnly,

    /// <summary>
    /// Only discover servers via mDNS and connect to them.
    /// Does not advertise or listen for incoming connections.
    /// </summary>
    DiscoverOnly
}
