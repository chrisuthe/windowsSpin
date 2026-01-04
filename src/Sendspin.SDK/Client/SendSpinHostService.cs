using Fleck;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Hosts a Sendspin client service that accepts incoming server connections.
/// This is the server-initiated mode where:
/// 1. We run a WebSocket server
/// 2. We advertise via mDNS as _sendspin._tcp.local.
/// 3. Sendspin servers discover and connect to us
/// </summary>
public sealed class SendspinHostService : IAsyncDisposable
{
    private readonly ILogger<SendspinHostService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SendspinListener _listener;
    private readonly MdnsServiceAdvertiser _advertiser;
    private readonly ClientCapabilities _capabilities;

    private readonly Dictionary<string, ActiveServerConnection> _connections = new();
    private readonly object _connectionsLock = new();

    /// <summary>
    /// Whether the host is running (listening and advertising).
    /// </summary>
    public bool IsRunning => _listener.IsListening && _advertiser.IsAdvertising;

    /// <summary>
    /// Whether the service is currently being advertised via mDNS.
    /// </summary>
    public bool IsAdvertising => _advertiser.IsAdvertising;

    /// <summary>
    /// The client ID being advertised.
    /// </summary>
    public string ClientId => _advertiser.ClientId;

    /// <summary>
    /// Currently connected servers.
    /// </summary>
    public IReadOnlyList<ConnectedServerInfo> ConnectedServers
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections.Values
                    .Where(c => c.Client.ConnectionState == ConnectionState.Connected)
                    .Select(c => new ConnectedServerInfo
                    {
                        ServerId = c.ServerId,
                        ServerName = c.Client.ServerName ?? c.ServerId,
                        ConnectedAt = c.ConnectedAt,
                        ClockSyncStatus = c.Client.ClockSyncStatus
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Raised when a new server connects and completes handshake.
    /// </summary>
    public event EventHandler<ConnectedServerInfo>? ServerConnected;

    /// <summary>
    /// Raised when a server disconnects.
    /// </summary>
    public event EventHandler<string>? ServerDisconnected;

    /// <summary>
    /// Raised when playback state changes on any connection.
    /// </summary>
    public event EventHandler<GroupState>? GroupStateChanged;

    /// <summary>
    /// Raised when artwork is received.
    /// </summary>
    public event EventHandler<byte[]>? ArtworkReceived;

    public SendspinHostService(
        ILoggerFactory loggerFactory,
        ClientCapabilities? capabilities = null,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinHostService>();
        _capabilities = capabilities ?? new ClientCapabilities();

        // Ensure options are consistent
        var listenOpts = listenerOptions ?? new ListenerOptions();
        var advertiseOpts = advertiserOptions ?? new AdvertiserOptions
        {
            ClientId = _capabilities.ClientId,
            Port = listenOpts.Port,
            Path = listenOpts.Path
        };

        _listener = new SendspinListener(
            loggerFactory.CreateLogger<SendspinListener>(),
            listenOpts);

        _advertiser = new MdnsServiceAdvertiser(
            loggerFactory.CreateLogger<MdnsServiceAdvertiser>(),
            advertiseOpts);

        _listener.ServerConnected += OnServerConnected;
    }

    /// <summary>
    /// Starts the host service (listener + mDNS advertisement).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Sendspin host service");

        // Start the WebSocket listener first
        await _listener.StartAsync(cancellationToken);

        // Then advertise via mDNS
        await _advertiser.StartAsync(cancellationToken);

        _logger.LogInformation("Sendspin host service started - waiting for server connections");
    }

    /// <summary>
    /// Stops the host service.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping Sendspin host service");

        // Stop advertising first
        await _advertiser.StopAsync();

        // Disconnect all clients
        List<ActiveServerConnection> connectionsToClose;
        lock (_connectionsLock)
        {
            connectionsToClose = _connections.Values.ToList();
            _connections.Clear();
        }

        foreach (var conn in connectionsToClose)
        {
            try
            {
                await conn.Client.DisconnectAsync("host_stopping");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from {ServerId}", conn.ServerId);
            }
        }

        // Stop the listener
        await _listener.StopAsync();

        _logger.LogInformation("Sendspin host service stopped");
    }

    /// <summary>
    /// Stops mDNS advertising without stopping the listener.
    /// Call this when manually connecting to a server to prevent
    /// other servers from trying to connect to this client.
    /// </summary>
    public async Task StopAdvertisingAsync()
    {
        if (!_advertiser.IsAdvertising)
            return;

        _logger.LogInformation("Stopping mDNS advertisement (manual connection active)");
        await _advertiser.StopAsync();
    }

    /// <summary>
    /// Resumes mDNS advertising after it was stopped.
    /// Call this when disconnecting from a manually connected server
    /// to allow servers to discover this client again.
    /// </summary>
    public async Task StartAdvertisingAsync(CancellationToken cancellationToken = default)
    {
        if (_advertiser.IsAdvertising)
            return;

        if (!_listener.IsListening)
        {
            _logger.LogWarning("Cannot start advertising - listener is not running");
            return;
        }

        _logger.LogInformation("Resuming mDNS advertisement");
        await _advertiser.StartAsync(cancellationToken);
    }

    private async void OnServerConnected(object? sender, IWebSocketConnection webSocket)
    {
        // All code must be inside try-catch since async void exceptions crash the app
        string? connectionId = null;
        try
        {
            connectionId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogInformation("New server connection: {ConnectionId}", connectionId);
            // Create connection wrapper for Fleck socket
            var connection = new IncomingConnection(
                _loggerFactory.CreateLogger<IncomingConnection>(),
                webSocket);

            // Create client service to handle the protocol
            var client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>()),
                _capabilities);

            // Subscribe to events
            client.GroupStateChanged += (s, g) => GroupStateChanged?.Invoke(this, g);
            client.ArtworkReceived += (s, data) => ArtworkReceived?.Invoke(this, data);
            client.ConnectionStateChanged += (s, e) => OnClientConnectionStateChanged(connectionId, e);

            // Start the connection (begins receive loop)
            await connection.StartAsync();

            // Send client hello - we always send this first per the protocol
            await SendClientHelloAsync(client, connection);

            // Wait for handshake to complete
            var handshakeComplete = new TaskCompletionSource<bool>();
            void OnStateChanged(object? s, ConnectionStateChangedEventArgs e)
            {
                if (e.NewState == ConnectionState.Connected)
                {
                    handshakeComplete.TrySetResult(true);
                }
                else if (e.NewState == ConnectionState.Disconnected)
                {
                    handshakeComplete.TrySetResult(false);
                }
            }

            client.ConnectionStateChanged += OnStateChanged;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => handshakeComplete.TrySetCanceled());

            try
            {
                var success = await handshakeComplete.Task;
                if (!success)
                {
                    _logger.LogWarning("Handshake failed for connection {ConnectionId}", connectionId);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Handshake timeout for connection {ConnectionId}", connectionId);
                await connection.DisconnectAsync("handshake_timeout");
                return;
            }
            finally
            {
                client.ConnectionStateChanged -= OnStateChanged;
            }

            // Store the active connection
            var serverId = client.ServerId ?? connectionId;
            var activeConnection = new ActiveServerConnection
            {
                ServerId = serverId,
                Client = client,
                Connection = connection,
                ConnectedAt = DateTime.UtcNow
            };

            lock (_connectionsLock)
            {
                _connections[serverId] = activeConnection;
            }

            _logger.LogInformation("Server connected: {ServerId} ({ServerName})",
                serverId, client.ServerName);

            ServerConnected?.Invoke(this, new ConnectedServerInfo
            {
                ServerId = serverId,
                ServerName = client.ServerName ?? serverId,
                ConnectedAt = activeConnection.ConnectedAt,
                ClockSyncStatus = client.ClockSyncStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling server connection {ConnectionId}", connectionId ?? "unknown");
        }
    }

    private async Task SendClientHelloAsync(SendspinClientService client, IncomingConnection connection)
    {
        var hello = ClientHelloMessage.Create(
            clientId: _capabilities.ClientId,
            name: _capabilities.ClientName,
            supportedRoles: _capabilities.Roles,
            playerSupport: new PlayerSupport
            {
                SupportedFormats = new List<AudioFormatSpec>
                {
                    new() { Codec = "pcm", Channels = 2, SampleRate = 44100, BitDepth = 16 }
                },
                BufferCapacity = _capabilities.BufferCapacity,
                SupportedCommands = new List<string> { "volume", "mute" }
            },
            artworkSupport: null,
            deviceInfo: new DeviceInfo
            {
                ProductName = _capabilities.ProductName,
                Manufacturer = _capabilities.Manufacturer,
                SoftwareVersion = _capabilities.SoftwareVersion
            }
        );

        var helloJson = MessageSerializer.Serialize(hello);
        _logger.LogInformation("Sending client/hello:\n{Json}", helloJson);
        await connection.SendMessageAsync(hello);
    }

    private void OnClientConnectionStateChanged(string connectionId, ConnectionStateChangedEventArgs e)
    {
        if (e.NewState == ConnectionState.Disconnected)
        {
            lock (_connectionsLock)
            {
                var entry = _connections.FirstOrDefault(c => c.Value.ServerId == connectionId);
                if (entry.Key != null)
                {
                    _connections.Remove(entry.Key);
                    _logger.LogInformation("Server disconnected: {ServerId}", entry.Key);
                    ServerDisconnected?.Invoke(this, entry.Key);
                }
            }
        }
    }

    /// <summary>
    /// Sends a command to a specific server or all connected servers.
    /// </summary>
    public async Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null, string? serverId = null)
    {
        List<SendspinClientService> clients;
        lock (_connectionsLock)
        {
            if (serverId != null)
            {
                if (_connections.TryGetValue(serverId, out var conn))
                {
                    clients = new List<SendspinClientService> { conn.Client };
                }
                else
                {
                    throw new InvalidOperationException($"Server {serverId} not connected");
                }
            }
            else
            {
                clients = _connections.Values.Select(c => c.Client).ToList();
            }
        }

        foreach (var client in clients)
        {
            await client.SendCommandAsync(command, parameters);
        }
    }

    /// <summary>
    /// Sends the current player state (volume, muted) to a specific server or all connected servers.
    /// </summary>
    public async Task SendPlayerStateAsync(int volume, bool muted, string? serverId = null)
    {
        List<SendspinClientService> clients;
        lock (_connectionsLock)
        {
            if (serverId != null)
            {
                if (_connections.TryGetValue(serverId, out var conn))
                {
                    clients = new List<SendspinClientService> { conn.Client };
                }
                else
                {
                    throw new InvalidOperationException($"Server {serverId} not connected");
                }
            }
            else
            {
                clients = _connections.Values.Select(c => c.Client).ToList();
            }
        }

        foreach (var client in clients)
        {
            await client.SendPlayerStateAsync(volume, muted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _listener.DisposeAsync();
        await _advertiser.DisposeAsync();
    }

    private class ActiveServerConnection
    {
        required public string ServerId { get; init; }
        required public SendspinClientService Client { get; init; }
        required public IncomingConnection Connection { get; init; }
        public DateTime ConnectedAt { get; init; }
    }
}

/// <summary>
/// Information about a connected Sendspin server.
/// </summary>
public record ConnectedServerInfo
{
    required public string ServerId { get; init; }
    required public string ServerName { get; init; }
    public DateTime ConnectedAt { get; init; }
    public ClockSyncStatus? ClockSyncStatus { get; init; }
}
