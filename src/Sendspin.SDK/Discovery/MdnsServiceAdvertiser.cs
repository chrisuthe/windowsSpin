using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Discovery;

/// <summary>
/// Advertises this client as a Sendspin service via mDNS.
/// This enables server-initiated connections where Sendspin servers
/// discover and connect to this client.
/// </summary>
public sealed class MdnsServiceAdvertiser : IAsyncDisposable
{
    private readonly ILogger<MdnsServiceAdvertiser> _logger;
    private readonly AdvertiserOptions _options;
    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _serviceProfile;
    private bool _disposed;

    /// <summary>
    /// Whether the service is currently being advertised.
    /// </summary>
    public bool IsAdvertising { get; private set; }

    /// <summary>
    /// The client ID being advertised.
    /// </summary>
    public string ClientId => _options.ClientId;

    public MdnsServiceAdvertiser(ILogger<MdnsServiceAdvertiser> logger, AdvertiserOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new AdvertiserOptions();
    }

    /// <summary>
    /// Starts advertising this client as a Sendspin service.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsAdvertising)
        {
            _logger.LogWarning("Already advertising");
            return Task.CompletedTask;
        }

        try
        {
            // Create the multicast DNS service
            _mdns = new MulticastService();

            // Log network interfaces being used
            _mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    _logger.LogDebug("mDNS using network interface: {Name} ({Id})",
                        nic.Name, nic.Id);
                }
            };

            // Log when queries are received (helps debug if mDNS is working)
            var queryCount = 0;
            _mdns.QueryReceived += (s, e) =>
            {
                foreach (var q in e.Message.Questions)
                {
                    // Log sendspin queries with high priority
                    if (q.Name.ToString().Contains("sendspin", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("*** Received mDNS query for SENDSPIN: {Name} (type={Type})",
                            q.Name, q.Type);
                    }
                    // Log first few queries to verify mDNS is working
                    else if (queryCount < 5)
                    {
                        _logger.LogDebug("Received mDNS query: {Name} (type={Type})",
                            q.Name, q.Type);
                        queryCount++;
                    }
                }
            };

            // Create service discovery for advertising
            _serviceDiscovery = new ServiceDiscovery(_mdns);

            // Get local IP addresses - filter out link-local addresses
            var addresses = GetLocalIPAddresses()
                .Where(ip => !ip.ToString().StartsWith("169.254.")) // Skip APIPA
                .ToList();

            _logger.LogInformation("Local IP addresses for mDNS: {Addresses}",
                string.Join(", ", addresses));

            if (addresses.Count == 0)
            {
                throw new InvalidOperationException("No valid network addresses found for mDNS advertising");
            }

            // Create the service profile
            // Service type: _sendspin._tcp.local.
            // Instance name: client ID
            _serviceProfile = new ServiceProfile(
                instanceName: _options.ClientId,
                serviceName: "_sendspin._tcp",
                port: (ushort)_options.Port,
                addresses: addresses);

            // Add TXT records - path must start with /
            _serviceProfile.AddProperty("path", _options.Path);
            // TODO: Re-enable once "name" TXT record is in official spec
            // _serviceProfile.AddProperty("name", _options.PlayerName);

            // Log the service profile details
            _logger.LogInformation(
                "mDNS Service Profile: FullName={FullName}, ServiceName={Service}, HostName={Host}, Port={Port}",
                _serviceProfile.FullyQualifiedName,
                _serviceProfile.ServiceName,
                _serviceProfile.HostName,
                _options.Port);

            // Log all resources being advertised
            foreach (var resource in _serviceProfile.Resources)
            {
                _logger.LogDebug("mDNS Resource: {Type} {Name}",
                    resource.GetType().Name, resource.Name);
            }

            // Advertise the service
            _serviceDiscovery.Advertise(_serviceProfile);

            // Start the multicast service
            _mdns.Start();

            IsAdvertising = true;
            _logger.LogInformation(
                "Advertising Sendspin client: {ClientId} on port {Port} (path={Path})",
                _options.ClientId, _options.Port, _options.Path);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mDNS advertising");
            throw;
        }
    }

    /// <summary>
    /// Stops advertising the service.
    /// </summary>
    public Task StopAsync()
    {
        if (!IsAdvertising)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping mDNS advertisement for {ClientId}", _options.ClientId);

        try
        {
            if (_serviceProfile != null && _serviceDiscovery != null)
            {
                _serviceDiscovery.Unadvertise(_serviceProfile);
            }

            _mdns?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping mDNS service");
        }
        finally
        {
            _serviceDiscovery = null;
            _serviceProfile = null;
            _mdns?.Dispose();
            _mdns = null;
            IsAdvertising = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets local IPv4 addresses for this machine, preferring interfaces with a default gateway.
    /// This filters out virtual adapters (Hyper-V, WSL, Docker) that aren't reachable from the LAN.
    /// </summary>
    private IEnumerable<IPAddress> GetLocalIPAddresses()
    {
        var gatewayAddresses = new List<IPAddress>();
        var allAddresses = new List<IPAddress>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                       && !g.Address.Equals(IPAddress.Any));

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    allAddresses.Add(addr.Address);
                    if (hasGateway)
                    {
                        gatewayAddresses.Add(addr.Address);
                    }
                }
            }
        }

        // Prefer interfaces with a gateway (connected to a real network).
        // Fall back to all addresses only if no gateway interfaces exist.
        var result = gatewayAddresses.Count > 0 ? gatewayAddresses : allAddresses;
        foreach (var addr in result)
        {
            yield return addr;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }
}

/// <summary>
/// Configuration options for mDNS service advertising.
/// </summary>
public sealed class AdvertiserOptions
{
    /// <summary>
    /// Unique client identifier.
    /// Default: sendspin-windows-{hostname}
    /// </summary>
    public string ClientId { get; set; } = $"sendspin-windows-{Environment.MachineName.ToLowerInvariant()}";

    /// <summary>
    /// Human-readable player name (advertised in TXT record as "name").
    /// Allows servers to display a friendly name during mDNS discovery,
    /// before the WebSocket handshake occurs.
    /// Default: machine name
    /// </summary>
    public string PlayerName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Port the WebSocket server is listening on.
    /// Default: 8928
    /// </summary>
    public int Port { get; set; } = 8928;

    /// <summary>
    /// WebSocket endpoint path (advertised in TXT record).
    /// Default: /sendspin
    /// </summary>
    public string Path { get; set; } = "/sendspin";
}
