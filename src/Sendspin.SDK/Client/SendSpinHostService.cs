using Fleck;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Extensions;
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
    private readonly IAudioPipeline? _audioPipeline;
    private readonly IClockSynchronizer? _clockSynchronizer;

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

    /// <summary>
    /// Raised when the last-played server ID changes.
    /// Consumers should persist this value so it survives app restarts.
    /// </summary>
    public event EventHandler<string>? LastPlayedServerIdChanged;

    /// <summary>
    /// Gets the server ID of the server that most recently had playback_state "playing".
    /// Used for tie-breaking when multiple servers with the same connection_reason try to connect.
    /// </summary>
    public string? LastPlayedServerId { get; private set; }

    /// <summary>
    /// Updates the last-played server ID.
    /// Call this when a server transitions to the "playing" state, regardless of connection mode.
    /// </summary>
    /// <param name="serverId">The server ID that is now playing.</param>
    public void SetLastPlayedServerId(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || serverId == LastPlayedServerId)
            return;

        LastPlayedServerId = serverId;
        _logger.LogInformation("Last played server updated: {ServerId}", serverId);
        LastPlayedServerIdChanged?.Invoke(this, serverId);
    }

    public SendspinHostService(
        ILoggerFactory loggerFactory,
        ClientCapabilities? capabilities = null,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null,
        IAudioPipeline? audioPipeline = null,
        IClockSynchronizer? clockSynchronizer = null,
        string? lastPlayedServerId = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinHostService>();
        _capabilities = capabilities ?? new ClientCapabilities();
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        LastPlayedServerId = lastPlayedServerId;

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

    /// <summary>
    /// Disconnects all currently connected servers.
    /// Use when switching to a client-initiated connection to ensure
    /// only one connection is using the audio pipeline at a time.
    /// </summary>
    public async Task DisconnectAllAsync(string reason = "switching_connection_mode")
    {
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
                _logger.LogInformation("Disconnecting server {ServerId}: {Reason}", conn.ServerId, reason);
                await conn.Client.DisconnectAsync(reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from {ServerId}", conn.ServerId);
            }
        }
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
            // Use the shared clock synchronizer if provided, otherwise create a per-connection one
            var clockSync = _clockSynchronizer
                ?? new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>());
            var client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                clockSync,
                _capabilities,
                _audioPipeline);

            // Subscribe to forwarded events (GroupState, Artwork)
            client.GroupStateChanged += (s, g) =>
            {
                // Track which server last had playback_state "playing"
                if (g.PlaybackState == PlaybackState.Playing && client.ServerId is not null)
                {
                    SetLastPlayedServerId(client.ServerId);
                }

                GroupStateChanged?.Invoke(this, g);
            };
            client.ArtworkReceived += (s, data) => ArtworkReceived?.Invoke(this, data);

            // Start the connection (begins receive loop)
            await connection.StartAsync();

            // Send client hello - we always send this first per the protocol
            await SendClientHelloAsync(client, connection);

            // Wait for handshake to complete
            if (!await WaitForHandshakeAsync(client, connection, connectionId))
            {
                return;
            }

            // Handshake complete - now arbitrate whether to accept this server
            var serverId = client.ServerId ?? connectionId;

            // Perform multi-server arbitration: determine whether the new server
            // should replace the existing one or be rejected
            if (!await ArbitrateConnectionAsync(client, connection, serverId))
            {
                // New server lost arbitration - it has already been disconnected
                return;
            }

            // Subscribe to connection state AFTER handshake so we use the correct serverId
            client.ConnectionStateChanged += (s, e) => OnClientConnectionStateChanged(serverId, e);
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
        // Use audio formats from capabilities (order matters - server picks first supported)
        var hello = ClientHelloMessage.Create(
            clientId: _capabilities.ClientId,
            name: _capabilities.ClientName,
            supportedRoles: _capabilities.Roles,
            playerSupport: new PlayerSupport
            {
                SupportedFormats = _capabilities.AudioFormats
                    .Select(f => new AudioFormatSpec
                    {
                        Codec = f.Codec,
                        Channels = f.Channels,
                        SampleRate = f.SampleRate,
                        BitDepth = f.BitDepth ?? 16,
                    })
                    .ToList(),
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

    /// <summary>
    /// Waits for the handshake to complete with timeout.
    /// </summary>
    /// <param name="client">The client service to monitor.</param>
    /// <param name="connection">The connection to disconnect on timeout.</param>
    /// <param name="connectionId">Connection ID for logging.</param>
    /// <param name="timeoutSeconds">Handshake timeout in seconds (default: 10).</param>
    /// <returns>True if handshake completed successfully, false otherwise.</returns>
    private async Task<bool> WaitForHandshakeAsync(
        SendspinClientService client,
        IncomingConnection connection,
        string connectionId,
        int timeoutSeconds = 10)
    {
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => handshakeComplete.TrySetCanceled());

        try
        {
            var success = await handshakeComplete.Task;
            if (!success)
            {
                _logger.LogWarning("Handshake failed for connection {ConnectionId}", connectionId);
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Handshake timeout for connection {ConnectionId}", connectionId);
            await connection.DisconnectAsync("handshake_timeout");
            return false;
        }
        finally
        {
            client.ConnectionStateChanged -= OnStateChanged;
        }
    }

    /// <summary>
    /// Arbitrates whether a newly handshaked server should become the active connection.
    /// Only one server can be active at a time. Priority rules:
    /// 1. "playback" connection_reason beats "discovery"
    /// 2. If tied, the last-played server wins
    /// 3. If still tied (or LastPlayedServerId is null), the existing server wins
    /// </summary>
    /// <param name="newClient">The new client that just completed handshake.</param>
    /// <param name="newConnection">The new connection to disconnect if rejected.</param>
    /// <param name="newServerId">The server ID of the new connection.</param>
    /// <returns>True if the new server is accepted, false if rejected.</returns>
    private async Task<bool> ArbitrateConnectionAsync(
        SendspinClientService newClient,
        IncomingConnection newConnection,
        string newServerId)
    {
        ActiveServerConnection? existingConnection = null;

        lock (_connectionsLock)
        {
            // Find the current active connection (there should be at most one)
            existingConnection = _connections.Values.FirstOrDefault();
        }

        // No existing server - accept the new one unconditionally
        if (existingConnection is null)
        {
            _logger.LogInformation(
                "Arbitration: Accepting {NewServerId} (no existing connection)",
                newServerId);
            return true;
        }

        var existingServerId = existingConnection.ServerId;

        // If the same server is reconnecting, accept it (replace the stale entry)
        if (string.Equals(newServerId, existingServerId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Arbitration: Accepting {NewServerId} (same server reconnecting)",
                newServerId);

            // Disconnect the old connection cleanly
            await DisconnectExistingAsync(existingConnection, "reconnecting");
            return true;
        }

        // Normalize connection reasons: null is treated as "discovery"
        var newReason = newClient.ConnectionReason ?? "discovery";
        var existingReason = existingConnection.Client.ConnectionReason ?? "discovery";

        bool newWins;
        string decision;

        if (string.Equals(newReason, "playback", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(existingReason, "playback", StringComparison.OrdinalIgnoreCase))
        {
            // New server has playback reason, existing does not - new wins
            newWins = true;
            decision = "new server has playback reason";
        }
        else if (string.Equals(existingReason, "playback", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(newReason, "playback", StringComparison.OrdinalIgnoreCase))
        {
            // Existing server has playback reason, new does not - existing wins
            newWins = false;
            decision = "existing server has playback reason";
        }
        else
        {
            // Tie: both have same reason - check LastPlayedServerId
            if (LastPlayedServerId is not null
                && string.Equals(newServerId, LastPlayedServerId, StringComparison.Ordinal))
            {
                newWins = true;
                decision = "new server matches LastPlayedServerId (tie-break)";
            }
            else
            {
                // Existing wins by default (including when LastPlayedServerId is null)
                newWins = false;
                decision = LastPlayedServerId is not null
                    ? "existing server wins tie-break (new server is not LastPlayedServerId)"
                    : "existing server wins tie-break (no LastPlayedServerId set)";
            }
        }

        _logger.LogInformation(
            "Arbitration: {Winner} wins. New={NewServerId} (reason={NewReason}), " +
            "Existing={ExistingServerId} (reason={ExistingReason}). Decision: {Decision}",
            newWins ? newServerId : existingServerId,
            newServerId, newReason,
            existingServerId, existingReason,
            decision);

        if (newWins)
        {
            // Disconnect the existing server
            await DisconnectExistingAsync(existingConnection, "another_server");
            return true;
        }
        else
        {
            // Reject the new server
            _logger.LogInformation(
                "Arbitration: Rejecting {NewServerId}, sending goodbye",
                newServerId);

            try
            {
                await newConnection.DisconnectAsync("another_server");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting rejected server {ServerId}", newServerId);
            }

            return false;
        }
    }

    /// <summary>
    /// Disconnects an existing active server connection during arbitration.
    /// Removes the connection from the tracking dictionary and sends a goodbye message.
    /// </summary>
    /// <param name="existing">The existing connection to disconnect.</param>
    /// <param name="reason">The goodbye reason to send.</param>
    private async Task DisconnectExistingAsync(ActiveServerConnection existing, string reason)
    {
        lock (_connectionsLock)
        {
            _connections.Remove(existing.ServerId);
        }

        _logger.LogInformation(
            "Arbitration: Disconnecting existing server {ServerId} with reason {Reason}",
            existing.ServerId, reason);

        try
        {
            await existing.Client.DisconnectAsync(reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting existing server {ServerId} during arbitration",
                existing.ServerId);
        }

        ServerDisconnected?.Invoke(this, existing.ServerId);
    }

    private void OnClientConnectionStateChanged(string connectionId, ConnectionStateChangedEventArgs e)
    {
        if (e.NewState == ConnectionState.Disconnected)
        {
            lock (_connectionsLock)
            {
                var entry = _connections.FirstOrDefault(c => c.Value.ServerId == connectionId);
                // FirstOrDefault returns default(KeyValuePair) when not found, which has Key=null.
                // This check works because dictionary keys are never null (serverId falls back to GUID).
                if (entry.Key is not null)
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
