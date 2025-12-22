using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Zeroconf;

namespace SendSpinClient.Core.Discovery;

/// <summary>
/// Discovers SendSpin servers using mDNS/DNS-SD (Bonjour/Avahi).
/// </summary>
public sealed class MdnsServerDiscovery : IServerDiscovery
{
    /// <summary>
    /// mDNS service type for SendSpin servers (client-initiated connections).
    /// </summary>
    public const string ServiceType = "_sendspin-server._tcp.local.";

    /// <summary>
    /// Alternative service type for server-initiated connections.
    /// </summary>
    public const string ClientServiceType = "_sendspin._tcp.local.";

    private readonly ILogger<MdnsServerDiscovery> _logger;
    private readonly ConcurrentDictionary<string, DiscoveredServer> _servers = new();
    private readonly TimeSpan _serverTimeout = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _discoveryCts;
    private Task? _discoveryTask;
    private Timer? _cleanupTimer;
    private bool _disposed;

    public IReadOnlyCollection<DiscoveredServer> Servers => _servers.Values.ToList();

    public event EventHandler<DiscoveredServer>? ServerFound;
    public event EventHandler<DiscoveredServer>? ServerLost;
    public event EventHandler<DiscoveredServer>? ServerUpdated;

    public MdnsServerDiscovery(ILogger<MdnsServerDiscovery> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_discoveryCts is not null)
        {
            return Task.CompletedTask; // Already running
        }

        _logger.LogInformation("Starting mDNS discovery for {ServiceType}", ServiceType);

        _discoveryCts = new CancellationTokenSource();
        _discoveryTask = ContinuousDiscoveryAsync(_discoveryCts.Token);

        // Start cleanup timer to remove stale servers
        _cleanupTimer = new Timer(CleanupStaleServers, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping mDNS discovery");

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        if (_discoveryCts is not null)
        {
            await _discoveryCts.CancelAsync();

            if (_discoveryTask is not null)
            {
                try
                {
                    await _discoveryTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _discoveryCts.Dispose();
            _discoveryCts = null;
            _discoveryTask = null;
        }
    }

    public async Task<IReadOnlyList<DiscoveredServer>> ScanAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Scanning for SendSpin servers (timeout: {Timeout})", timeout);

        try
        {
            var results = await ZeroconfResolver.ResolveAsync(
                ServiceType,
                scanTime: timeout,
                cancellationToken: cancellationToken);

            var servers = new List<DiscoveredServer>();

            foreach (var host in results)
            {
                var server = ParseHost(host);
                if (server is not null)
                {
                    servers.Add(server);
                    UpdateServer(server);
                }
            }

            _logger.LogInformation("Scan found {Count} server(s)", servers.Count);
            return servers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during mDNS scan");
            return Array.Empty<DiscoveredServer>();
        }
    }

    private async Task ContinuousDiscoveryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Perform a scan
                await ScanAsync(TimeSpan.FromSeconds(5), cancellationToken);

                // Wait before next scan
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in continuous discovery, retrying...");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private DiscoveredServer? ParseHost(IZeroconfHost host)
    {
        try
        {
            var service = host.Services.Values.FirstOrDefault();
            if (service is null)
            {
                return null;
            }

            // Log the raw host info for debugging
            _logger.LogInformation("mDNS Host: {DisplayName}, IPs: [{IPs}], Port: {Port}",
                host.DisplayName,
                string.Join(", ", host.IPAddresses),
                service.Port);

            // Extract TXT record properties
            var properties = new Dictionary<string, string>();
            foreach (var prop in service.Properties)
            {
                foreach (var kvp in prop)
                {
                    properties[kvp.Key] = kvp.Value;
                    _logger.LogInformation("mDNS TXT: {Key} = {Value}", kvp.Key, kvp.Value);
                }
            }

            if (properties.Count == 0)
            {
                _logger.LogWarning("No TXT records found for host {Host}", host.DisplayName);
            }

            // Get server ID from TXT record or generate from name
            var serverId = properties.TryGetValue("id", out var id)
                ? id
                : properties.TryGetValue("server_id", out var sid)
                    ? sid
                    : $"{host.DisplayName}-{host.IPAddresses.FirstOrDefault()}";

            var server = new DiscoveredServer
            {
                ServerId = serverId,
                Name = properties.TryGetValue("name", out var name) ? name : host.DisplayName,
                Host = host.DisplayName,
                Port = service.Port,
                IpAddresses = host.IPAddresses.ToList(),
                ProtocolVersion = properties.TryGetValue("version", out var version) ? version : null,
                Properties = properties
            };

            return server;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse host {Host}", host.DisplayName);
            return null;
        }
    }

    private void UpdateServer(DiscoveredServer server)
    {
        if (_servers.TryGetValue(server.ServerId, out var existing))
        {
            // Update last seen time
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            ServerUpdated?.Invoke(this, existing);
        }
        else
        {
            // New server
            if (_servers.TryAdd(server.ServerId, server))
            {
                _logger.LogInformation("Discovered new server: {Server}", server);
                ServerFound?.Invoke(this, server);
            }
        }
    }

    private void CleanupStaleServers(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _serverTimeout;
        var staleServers = _servers.Values
            .Where(s => s.LastSeenAt < cutoff)
            .ToList();

        foreach (var server in staleServers)
        {
            if (_servers.TryRemove(server.ServerId, out _))
            {
                _logger.LogInformation("Server lost (timeout): {Server}", server);
                ServerLost?.Invoke(this, server);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }
}
