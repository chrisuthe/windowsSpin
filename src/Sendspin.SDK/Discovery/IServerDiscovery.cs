namespace Sendspin.SDK.Discovery;

/// <summary>
/// Interface for discovering Sendspin servers on the network.
/// </summary>
public interface IServerDiscovery : IAsyncDisposable
{
    /// <summary>
    /// Currently known servers.
    /// </summary>
    IReadOnlyCollection<DiscoveredServer> Servers { get; }

    /// <summary>
    /// Starts continuous discovery.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops discovery.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Performs a one-time scan for servers.
    /// </summary>
    /// <param name="timeout">Scan timeout</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered servers</returns>
    Task<IReadOnlyList<DiscoveredServer>> ScanAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when a new server is discovered.
    /// </summary>
    event EventHandler<DiscoveredServer>? ServerFound;

    /// <summary>
    /// Event raised when a server is no longer available.
    /// </summary>
    event EventHandler<DiscoveredServer>? ServerLost;

    /// <summary>
    /// Event raised when a server's information is updated.
    /// </summary>
    event EventHandler<DiscoveredServer>? ServerUpdated;
}
