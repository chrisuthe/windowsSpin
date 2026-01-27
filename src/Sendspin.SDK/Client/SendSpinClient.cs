using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Main Sendspin client that orchestrates connection, handshake, and message handling.
/// </summary>
public sealed class SendspinClientService : ISendspinClient
{
    private readonly ILogger<SendspinClientService> _logger;
    private readonly ISendspinConnection _connection;
    private readonly ClientCapabilities _capabilities;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly IAudioPipeline? _audioPipeline;

    private TaskCompletionSource<bool>? _handshakeTcs;
    private GroupState? _currentGroup;
    private PlayerState _playerState;
    private CancellationTokenSource? _timeSyncCts;
    private bool _isReconnecting;
    private bool _disposed;

    /// <summary>
    /// Queue for audio chunks that arrive before pipeline is ready.
    /// Prevents chunk loss during the ~50ms decoder/buffer initialization.
    /// </summary>
    private readonly ConcurrentQueue<AudioChunk> _earlyChunkQueue = new();

    /// <summary>
    /// Maximum chunks to queue before pipeline ready (~2 seconds of audio at typical rates).
    /// </summary>
    private const int MaxEarlyChunks = 100;

    #region Time Sync Configuration

    /// <summary>
    /// Number of time sync messages to send in a burst.
    /// The measurement with the smallest round-trip time is used for best accuracy.
    /// </summary>
    /// <remarks>
    /// Sending multiple messages allows us to identify network jitter and select
    /// the cleanest measurement. A burst of 8 typically yields at least one
    /// measurement with minimal queuing delay.
    /// </remarks>
    private const int BurstSize = 8;

    /// <summary>
    /// Interval in milliseconds between burst messages.
    /// Short enough for quick bursts, long enough to avoid packet queuing.
    /// </summary>
    private const int BurstIntervalMs = 50;

    #endregion

    private readonly object _burstLock = new();
    private readonly List<(long t1, long t2, long t3, long t4, double rtt)> _burstResults = new();
    private readonly HashSet<long> _pendingBurstTimestamps = new();

    public ConnectionState ConnectionState => _connection.State;
    public string? ServerId { get; private set; }
    public string? ServerName { get; private set; }

    /// <summary>
    /// The connection reason provided by the server in the server/hello handshake.
    /// Typically "discovery" (server found us via mDNS) or "playback" (server needs us for active playback).
    /// Used for multi-server arbitration in the host service.
    /// </summary>
    public string? ConnectionReason { get; private set; }

    public GroupState? CurrentGroup => _currentGroup;
    public PlayerState CurrentPlayerState => _playerState;
    public ClockSyncStatus? ClockSyncStatus => _clockSynchronizer.GetStatus();
    public bool IsClockSynced => _clockSynchronizer.IsConverged;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<GroupState>? GroupStateChanged;
    public event EventHandler<PlayerState>? PlayerStateChanged;
    public event EventHandler<byte[]>? ArtworkReceived;
    public event EventHandler<ClockSyncStatus>? ClockSyncConverged;
    public event EventHandler<SyncOffsetEventArgs>? SyncOffsetApplied;

    public SendspinClientService(
        ILogger<SendspinClientService> logger,
        ISendspinConnection connection,
        IClockSynchronizer? clockSynchronizer = null,
        ClientCapabilities? capabilities = null,
        IAudioPipeline? audioPipeline = null)
    {
        _logger = logger;
        _connection = connection;
        _clockSynchronizer = clockSynchronizer ?? new KalmanClockSynchronizer();
        _capabilities = capabilities ?? new ClientCapabilities();
        _audioPipeline = audioPipeline;

        // Initialize player state from capabilities
        _playerState = new PlayerState
        {
            Volume = Math.Clamp(_capabilities.InitialVolume, 0, 100),
            Muted = _capabilities.InitialMuted
        };

        // Subscribe to connection events
        _connection.StateChanged += OnConnectionStateChanged;
        _connection.TextMessageReceived += OnTextMessageReceived;
        _connection.BinaryMessageReceived += OnBinaryMessageReceived;
    }

    public async Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logger.LogInformation("Connecting to {Uri}", serverUri);

        // Connect WebSocket
        await _connection.ConnectAsync(serverUri, cancellationToken);

        // Perform handshake (send client hello, wait for server hello)
        await SendHandshakeAsync(cancellationToken);
    }

    /// <summary>
    /// Sends the ClientHello message and waits for the ServerHello response.
    /// Used for both initial connection and reconnection handshakes.
    /// </summary>
    private async Task SendHandshakeAsync(CancellationToken cancellationToken = default)
    {
        _handshakeTcs = new TaskCompletionSource<bool>();

        var hello = CreateClientHelloMessage();
        var helloJson = MessageSerializer.Serialize(hello);
        _logger.LogInformation("Sending client/hello:\n{Json}", helloJson);
        await _connection.SendMessageAsync(hello, cancellationToken);

        // Wait for server hello with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var registration = linkedCts.Token.Register(() => _handshakeTcs.TrySetCanceled());
            var success = await _handshakeTcs.Task;

            if (success)
            {
                _logger.LogInformation("Handshake complete with server {ServerId} ({ServerName})", ServerId, ServerName);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogError("Handshake timeout - no server/hello received");
            await _connection.DisconnectAsync("handshake_timeout");
            throw new TimeoutException("Server did not respond to handshake");
        }
    }

    /// <summary>
    /// Creates the ClientHello message from current capabilities.
    /// Extracted for reuse between initial connection and reconnection handshakes.
    /// </summary>
    private ClientHelloMessage CreateClientHelloMessage()
    {
        return ClientHelloMessage.Create(
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
            artworkSupport: new ArtworkSupport
            {
                Channels = new List<ArtworkChannelSpec>
                {
                    new ArtworkChannelSpec
                    {
                        Source = "album",
                        Format = _capabilities.ArtworkFormats.FirstOrDefault() ?? "jpeg",
                        MediaWidth = _capabilities.ArtworkMaxSize,
                        MediaHeight = _capabilities.ArtworkMaxSize
                    }
                }
            },
            deviceInfo: new DeviceInfo
            {
                ProductName = _capabilities.ProductName,
                Manufacturer = _capabilities.Manufacturer,
                SoftwareVersion = _capabilities.SoftwareVersion
            }
        );
    }

    /// <summary>
    /// Performs handshake after the connection layer has successfully reconnected the WebSocket.
    /// Called from OnConnectionStateChanged when entering Handshaking state during reconnection.
    /// Resets the clock synchronizer and sends a fresh ClientHello.
    /// </summary>
    private async Task PerformReconnectHandshakeAsync()
    {
        _logger.LogInformation("WebSocket reconnected, performing handshake...");

        // Reset clock synchronizer for the new connection
        _clockSynchronizer.Reset();

        try
        {
            await SendHandshakeAsync();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Reconnect handshake timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect handshake failed");
            await _connection.DisconnectAsync("handshake_failed");
        }
    }

    public async Task DisconnectAsync(string reason = "user_request")
    {
        if (_disposed) return;

        _logger.LogInformation("Disconnecting: {Reason}", reason);

        // Stop time sync loop
        StopTimeSyncLoop();

        await _connection.DisconnectAsync(reason);

        ServerId = null;
        ServerName = null;
        ConnectionReason = null;
        _currentGroup = null;
    }

    public async Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null)
    {
        // Extract volume and mute from parameters if present
        int? volume = null;
        bool? mute = null;

        if (parameters != null)
        {
            if (parameters.TryGetValue("volume", out var volObj) && volObj is int vol)
            {
                volume = vol;
            }

            if (parameters.TryGetValue("muted", out var muteObj) && muteObj is bool m)
            {
                mute = m;
            }
        }

        var message = ClientCommandMessage.Create(command, volume, mute);

        _logger.LogDebug("Sending command: {Command}", command);
        await _connection.SendMessageAsync(message);
    }

    public async Task SetVolumeAsync(int volume)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        var message = ClientCommandMessage.Create(Commands.Volume, volume: clampedVolume);

        _logger.LogDebug("Setting volume to {Volume}", clampedVolume);
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task SendPlayerStateAsync(int volume, bool muted)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        var stateMessage = ClientStateMessage.CreateSynchronized(clampedVolume, muted);

        _logger.LogDebug("Sending player state: Volume={Volume}, Muted={Muted}", clampedVolume, muted);
        await _connection.SendMessageAsync(stateMessage);
    }

    /// <inheritdoc/>
    public void ClearAudioBuffer()
    {
        _logger.LogDebug("Clearing audio buffer for immediate sync parameter effect");
        _audioPipeline?.Clear();
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogDebug("Connection state: {OldState} -> {NewState}", e.OldState, e.NewState);

        // Forward the event
        ConnectionStateChanged?.Invoke(this, e);

        // Stop time sync on any disconnection-related state to prevent
        // "WebSocket is not connected" spam from the time sync loop
        if (e.NewState is ConnectionState.Disconnected or ConnectionState.Reconnecting)
        {
            StopTimeSyncLoop();
        }

        // Clean up client state on full disconnection
        if (e.NewState == ConnectionState.Disconnected)
        {
            _handshakeTcs?.TrySetResult(false);
            ServerId = null;
            ServerName = null;
            ConnectionReason = null;
        }

        // Re-handshake when WebSocket reconnects successfully
        if (e.NewState == ConnectionState.Handshaking && _isReconnecting)
        {
            PerformReconnectHandshakeAsync().SafeFireAndForget(_logger);
        }

        // Track reconnection state
        if (e.NewState == ConnectionState.Reconnecting)
        {
            _isReconnecting = true;
        }
        else if (e.NewState is ConnectionState.Connected or ConnectionState.Disconnected)
        {
            _isReconnecting = false;
        }
    }

    private void OnTextMessageReceived(object? sender, string json)
    {
        try
        {
            var messageType = MessageSerializer.GetMessageType(json);
            _logger.LogTrace("Received: {Type}", messageType);

            switch (messageType)
            {
                case MessageTypes.ServerHello:
                    HandleServerHello(json);
                    break;

                case MessageTypes.ServerTime:
                    HandleServerTime(json);
                    break;

                case MessageTypes.GroupUpdate:
                    HandleGroupUpdate(json);
                    break;

                case MessageTypes.StreamStart:
                    HandleStreamStartAsync(json).SafeFireAndForget(_logger);
                    break;

                case MessageTypes.StreamEnd:
                    HandleStreamEndAsync(json).SafeFireAndForget(_logger);
                    break;

                case MessageTypes.StreamClear:
                    HandleStreamClear(json);
                    break;

                case MessageTypes.ServerState:
                    HandleServerState(json);
                    break;

                case MessageTypes.ServerCommand:
                    HandleServerCommand(json);
                    break;

                case MessageTypes.ClientSyncOffset:
                    HandleSyncOffset(json);
                    break;

                default:
                    _logger.LogDebug("Unhandled message type: {Type}", messageType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
    }

    private void HandleServerHello(string json)
    {
        var message = MessageSerializer.Deserialize<ServerHelloMessage>(json);
        if (message is null)
        {
            _logger.LogWarning("Failed to deserialize server/hello");
            _handshakeTcs?.TrySetResult(false);
            return;
        }

        ServerId = message.ServerId;
        ServerName = message.Name;
        ConnectionReason = message.Payload.ConnectionReason;

        _logger.LogInformation("Server hello received: {ServerId} ({ServerName}), reason: {ConnectionReason}, roles: {Roles}",
            message.ServerId, message.Name, ConnectionReason ?? "none", string.Join(", ", message.ActiveRoles));

        // Mark connection as fully connected
        if (_connection is SendspinConnection conn)
        {
            conn.MarkConnected();
        }
        else if (_connection is IncomingConnection incoming)
        {
            incoming.MarkConnected();
        }

        // Reset clock synchronizer for new connection
        _clockSynchronizer.Reset();

        // Send initial client state (required by protocol after server/hello)
        // This tells the server we're synchronized and ready
        SendInitialClientStateAsync().SafeFireAndForget(_logger);

        // Start time synchronization loop with adaptive intervals
        StartTimeSyncLoop();

        _handshakeTcs?.TrySetResult(true);
    }

    /// <summary>
    /// Sends the initial client/state message after handshake.
    /// Per the protocol, clients with player role must send their state immediately.
    /// Uses the current <see cref="_playerState"/> which was initialized from ClientCapabilities.
    /// </summary>
    private async Task SendInitialClientStateAsync()
    {
        try
        {
            // Send the current player state (initialized from capabilities)
            var stateMessage = ClientStateMessage.CreateSynchronized(
                volume: _playerState.Volume,
                muted: _playerState.Muted);
            var stateJson = MessageSerializer.Serialize(stateMessage);
            _logger.LogInformation("Sending initial client/state:\n{Json}", stateJson);
            await _connection.SendMessageAsync(stateMessage);

            // Also apply to audio pipeline to ensure consistency
            _audioPipeline?.SetVolume(_playerState.Volume);
            _audioPipeline?.SetMuted(_playerState.Muted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send initial client state");
        }
    }

    private void StartTimeSyncLoop()
    {
        // Stop existing loop if any
        StopTimeSyncLoop();

        _timeSyncCts = new CancellationTokenSource();

        // Fire-and-forget: task runs until cancelled, no need to await
        _ = TimeSyncLoopAsync(_timeSyncCts.Token);

        _logger.LogDebug("Time sync loop started (adaptive intervals)");
    }

    private void StopTimeSyncLoop()
    {
        _timeSyncCts?.Cancel();
        _timeSyncCts?.Dispose();
        _timeSyncCts = null;
        _logger.LogDebug("Time sync loop stopped");
    }

    /// <summary>
    /// Calculates the next time sync interval based on synchronization quality.
    /// Uses longer intervals when well-synced to improve drift measurement signal-to-noise ratio.
    /// </summary>
    private int GetAdaptiveTimeSyncIntervalMs()
    {
        var status = _clockSynchronizer.GetStatus();

        // If not enough measurements yet, sync rapidly (but after burst, so this is inter-burst interval)
        if (status.MeasurementCount < 3)
            return 500; // 500ms between initial bursts

        // Uncertainty in milliseconds
        var uncertaintyMs = status.OffsetUncertaintyMicroseconds / 1000.0;

        // Adaptive intervals based on sync quality
        // Longer intervals when synced = better drift signal detection over time
        if (uncertaintyMs < 1.0)
            return 10000; // Well synchronized: 10s (allows drift to accumulate measurably)
        else if (uncertaintyMs < 2.0)
            return 5000;  // Good sync: 5s
        else if (uncertaintyMs < 5.0)
            return 2000;  // Moderate sync: 2s
        else
            return 1000;  // Poor sync: 1s
    }

    private async Task TimeSyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _connection.State == ConnectionState.Connected)
            {
                // Send burst of time sync messages
                await SendTimeSyncBurstAsync(cancellationToken);

                // Calculate adaptive interval based on current sync quality
                var intervalMs = GetAdaptiveTimeSyncIntervalMs();

                _logger.LogTrace("Next time sync burst in {Interval}ms (uncertainty: {Uncertainty:F2}ms)",
                    intervalMs,
                    _clockSynchronizer.GetStatus().OffsetUncertaintyMicroseconds / 1000.0);

                // Wait for the interval before next burst
                await Task.Delay(intervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time sync loop ended unexpectedly");
        }
    }

    /// <summary>
    /// Sends a burst of time sync messages and waits for responses.
    /// Only the measurement with the smallest RTT is used (best quality).
    /// </summary>
    private async Task SendTimeSyncBurstAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Connected)
            return;

        try
        {
            // Clear previous burst results
            lock (_burstLock)
            {
                _burstResults.Clear();
                _pendingBurstTimestamps.Clear();
            }

            // Send burst of messages
            for (int i = 0; i < BurstSize; i++)
            {
                if (cancellationToken.IsCancellationRequested || _connection.State != ConnectionState.Connected)
                    break;

                var timeMessage = ClientTimeMessage.CreateNow();

                lock (_burstLock)
                {
                    _pendingBurstTimestamps.Add(timeMessage.ClientTransmitted);
                }

                await _connection.SendMessageAsync(timeMessage, cancellationToken);
                _logger.LogTrace("Sent burst message {Index}/{Total}: T1={T1}", i + 1, BurstSize, timeMessage.ClientTransmitted);

                // Wait between burst messages (except after last one)
                if (i < BurstSize - 1)
                {
                    await Task.Delay(BurstIntervalMs, cancellationToken);
                }
            }

            // Wait for responses to arrive (give extra time after last send)
            await Task.Delay(BurstIntervalMs * 2, cancellationToken);

            // Process the best result from the burst
            ProcessBurstResults();
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send time sync burst");
        }
    }

    /// <summary>
    /// Processes collected burst results and feeds the best measurement to the Kalman filter.
    /// </summary>
    private void ProcessBurstResults()
    {
        (long t1, long t2, long t3, long t4, double rtt) bestResult;
        int totalResults;

        lock (_burstLock)
        {
            totalResults = _burstResults.Count;
            if (totalResults == 0)
            {
                _logger.LogDebug("No burst results to process");
                return;
            }

            // Find the measurement with smallest RTT (best quality)
            bestResult = _burstResults.OrderBy(r => r.rtt).First();
            _burstResults.Clear();
            _pendingBurstTimestamps.Clear();
        }

        _logger.LogDebug("Processing best of {Count} burst results: RTT={RTT:F0}μs",
            totalResults, bestResult.rtt);

        // Track if we were already converged before this measurement
        bool wasConverged = _clockSynchronizer.IsConverged;

        // Feed only the best measurement to the Kalman filter
        _clockSynchronizer.ProcessMeasurement(bestResult.t1, bestResult.t2, bestResult.t3, bestResult.t4);

        // Log the sync status periodically
        var status = _clockSynchronizer.GetStatus();
        if (status.MeasurementCount <= 10 || status.MeasurementCount % 10 == 0)
        {
            _logger.LogInformation(
                "Clock sync: offset={Offset:F2}ms (±{Uncertainty:F2}ms), drift={Drift:F2}μs/s, converged={Converged}, driftReliable={DriftReliable}",
                status.OffsetMilliseconds,
                status.OffsetUncertaintyMicroseconds / 1000.0,
                status.DriftMicrosecondsPerSecond,
                status.IsConverged,
                status.IsDriftReliable);
        }

        // Notify when first converged
        if (!wasConverged && _clockSynchronizer.IsConverged)
        {
            _logger.LogInformation("Clock synchronization converged after {Count} measurements", status.MeasurementCount);
            ClockSyncConverged?.Invoke(this, status);
        }
    }

    private void HandleServerTime(string json)
    {
        var message = MessageSerializer.Deserialize<ServerTimeMessage>(json);
        if (message is null) return;

        // Record receive time (T4)
        var t4 = ClientTimeMessage.GetCurrentTimestampMicroseconds();

        // NTP timestamps: T1=client sent, T2=server received, T3=server sent, T4=client received
        var t1 = message.ClientTransmitted;
        var t2 = message.ServerReceived;
        var t3 = message.ServerTransmitted;

        // Calculate RTT: (T4 - T1) - (T3 - T2) = network round-trip excluding server processing
        double rtt = (t4 - t1) - (t3 - t2);

        lock (_burstLock)
        {
            // Check if this is a response to a burst message we sent
            if (_pendingBurstTimestamps.Contains(t1))
            {
                // Collect this result for later processing
                _burstResults.Add((t1, t2, t3, t4, rtt));
                _pendingBurstTimestamps.Remove(t1);
                _logger.LogTrace("Collected burst response: RTT={RTT:F0}μs ({Count} collected)",
                    rtt, _burstResults.Count);
                return;
            }
        }

        // If not part of a burst (shouldn't happen normally), process immediately
        _logger.LogDebug("Processing non-burst time response: RTT={RTT:F0}μs", rtt);
        _clockSynchronizer.ProcessMeasurement(t1, t2, t3, t4);
    }

    private void HandleGroupUpdate(string json)
    {
        var message = MessageSerializer.Deserialize<GroupUpdateMessage>(json);
        if (message is null) return;

        // Create group state if needed
        _currentGroup ??= new GroupState();

        var previousGroupId = _currentGroup.GroupId;
        var previousName = _currentGroup.Name;

        // group/update contains: group_id, group_name, playback_state
        // Volume, mute, metadata come via server/state (handled in HandleServerState)
        if (!string.IsNullOrEmpty(message.GroupId))
            _currentGroup.GroupId = message.GroupId;
        if (!string.IsNullOrEmpty(message.GroupName))
            _currentGroup.Name = message.GroupName;
        if (message.PlaybackState.HasValue)
            _currentGroup.PlaybackState = message.PlaybackState.Value;

        // Log group ID changes (helps diagnose grouping issues)
        if (previousGroupId != _currentGroup.GroupId && !string.IsNullOrEmpty(previousGroupId))
        {
            _logger.LogInformation("group/update [{Player}]: Group ID changed {OldId} -> {NewId}",
                _capabilities.ClientName, previousGroupId, _currentGroup.GroupId);
        }

        // Log group name changes
        if (previousName != _currentGroup.Name && _currentGroup.Name is not null)
        {
            _logger.LogInformation("group/update [{Player}]: Group name changed '{OldName}' -> '{NewName}'",
                _capabilities.ClientName, previousName ?? "(none)", _currentGroup.Name);
        }

        _logger.LogInformation("group/update [{Player}]: GroupId={GroupId}, Name={Name}, State={State}",
            _capabilities.ClientName,
            _currentGroup.GroupId,
            _currentGroup.Name ?? "(none)",
            _currentGroup.PlaybackState);

        GroupStateChanged?.Invoke(this, _currentGroup);
    }

    private void HandleServerState(string json)
    {
        var message = MessageSerializer.Deserialize<ServerStateMessage>(json);
        if (message is null) return;

        var payload = message.Payload;
        _currentGroup ??= new GroupState();

        // Update metadata from server/state (merge with existing to preserve data across partial updates)
        if (payload.Metadata is not null)
        {
            var meta = payload.Metadata;
            var existing = _currentGroup.Metadata ?? new TrackMetadata();

            // Only update fields that are present in the message
            _currentGroup.Metadata = new TrackMetadata
            {
                Timestamp = meta.Timestamp ?? existing.Timestamp,
                Title = meta.Title ?? existing.Title,
                Artist = meta.Artist ?? existing.Artist,
                AlbumArtist = meta.AlbumArtist ?? existing.AlbumArtist,
                Album = meta.Album ?? existing.Album,
                ArtworkUrl = meta.ArtworkUrl ?? existing.ArtworkUrl,
                Year = meta.Year ?? existing.Year,
                Track = meta.Track ?? existing.Track,
                Progress = meta.Progress ?? existing.Progress,
                Repeat = meta.Repeat ?? existing.Repeat,
                Shuffle = meta.Shuffle ?? existing.Shuffle
            };

            // Update group-level shuffle/repeat from metadata
            if (meta.Shuffle.HasValue)
                _currentGroup.Shuffle = meta.Shuffle.Value;
            if (meta.Repeat is not null)
                _currentGroup.Repeat = meta.Repeat;
        }

        // Update controller state (volume, mute) for UI display only.
        // Do NOT apply to audio pipeline - server/state contains GROUP volume.
        // The server sends server/command with player-specific volume when it wants
        // to change THIS player's output.
        if (payload.Controller is not null)
        {
            if (payload.Controller.Volume.HasValue)
                _currentGroup.Volume = payload.Controller.Volume.Value;
            if (payload.Controller.Muted.HasValue)
                _currentGroup.Muted = payload.Controller.Muted.Value;
        }

        _logger.LogDebug("server/state [{Player}]: Volume={Volume}, Muted={Muted}, Track={Track} by {Artist}",
            _capabilities.ClientName,
            _currentGroup.Volume,
            _currentGroup.Muted,
            _currentGroup.Metadata?.Title ?? "unknown",
            _currentGroup.Metadata?.Artist ?? "unknown");

        GroupStateChanged?.Invoke(this, _currentGroup);
    }

    /// <summary>
    /// Handles server/command messages that instruct the player to apply volume or mute changes.
    /// These commands originate from controller clients and are relayed by the server to all players.
    /// </summary>
    /// <remarks>
    /// Per the Sendspin spec, after applying a server/command, the player MUST send a client/state
    /// message back to acknowledge the change. This allows the server to:
    /// 1. Confirm the player received and applied the command
    /// 2. Recalculate the group average from actual player states
    /// 3. Broadcast updated group state to controllers
    /// </remarks>
    private void HandleServerCommand(string json)
    {
        var message = MessageSerializer.Deserialize<ServerCommandMessage>(json);
        if (message?.Payload?.Player is null)
        {
            _logger.LogDebug("server/command: No player command in message");
            return;
        }

        var player = message.Payload.Player;
        var changed = false;

        _logger.LogDebug("server/command: {Command}", player.Command);

        // Apply volume change - update player state and audio pipeline
        // Note: This updates _playerState (THIS player's volume), not _currentGroup (group average)
        if (player.Volume.HasValue)
        {
            _playerState.Volume = player.Volume.Value;
            _audioPipeline?.SetVolume(player.Volume.Value);
            changed = true;
            _logger.LogInformation("server/command [{Player}]: Applied volume {Volume}",
                _capabilities.ClientName, player.Volume.Value);
        }

        // Apply mute change - update player state and audio pipeline
        if (player.Mute.HasValue)
        {
            _playerState.Muted = player.Mute.Value;
            _audioPipeline?.SetMuted(player.Mute.Value);
            changed = true;
            _logger.LogInformation("server/command [{Player}]: Applied mute {Muted}",
                _capabilities.ClientName, player.Mute.Value);
        }

        if (changed)
        {
            // Notify listeners of player state change
            PlayerStateChanged?.Invoke(this, _playerState);

            // ACK: send client/state to confirm applied state back to server.
            SendPlayerStateAckAsync().SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// Sends a client/state acknowledgement after applying a server/command.
    /// Reports current player volume and mute state back to the server.
    /// </summary>
    private async Task SendPlayerStateAckAsync()
    {
        await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted);
    }

    /// <summary>
    /// Handles client/sync_offset messages from GroupSync calibration tool.
    /// Applies the calculated offset to the static delay for speaker synchronization.
    /// </summary>
    private void HandleSyncOffset(string json)
    {
        var message = MessageSerializer.Deserialize<ClientSyncOffsetMessage>(json);
        if (message?.Payload is null)
        {
            _logger.LogWarning("client/sync_offset: Invalid message format");
            return;
        }

        var payload = message.Payload;
        _logger.LogInformation(
            "client/sync_offset: Applying offset {Offset}ms from {Source}",
            payload.OffsetMs,
            payload.Source ?? "unknown");

        // Clamp offset to reasonable range (-5000 to +5000 ms)
        const double MinOffset = -5000.0;
        const double MaxOffset = 5000.0;
        var clampedOffset = Math.Clamp(payload.OffsetMs, MinOffset, MaxOffset);

        if (Math.Abs(clampedOffset - payload.OffsetMs) > 0.001)
        {
            _logger.LogWarning(
                "client/sync_offset: Offset clamped from {Original}ms to {Clamped}ms",
                payload.OffsetMs,
                clampedOffset);
        }

        // Apply the offset to the clock synchronizer
        _clockSynchronizer.StaticDelayMs = clampedOffset;

        _logger.LogDebug("client/sync_offset: Static delay set to {Delay}ms", clampedOffset);

        // Raise event for UI notification
        SyncOffsetApplied?.Invoke(this, new SyncOffsetEventArgs(payload.PlayerId, clampedOffset, payload.Source));
    }

    private async Task HandleStreamStartAsync(string json)
    {
        var message = MessageSerializer.Deserialize<StreamStartMessage>(json);
        if (message is null)
        {
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", message.Format);

        // Clear any stale chunks from previous streams
        while (_earlyChunkQueue.TryDequeue(out _))
        {
        }

        // Smart sync burst: only trigger if clock isn't already synced
        // If we've been connected for a while, the continuous sync loop has already converged
        if (!_clockSynchronizer.HasMinimalSync)
        {
            _logger.LogDebug("Clock not synced, triggering re-sync burst (fire-and-forget)");
            _ = SendTimeSyncBurstAsync(CancellationToken.None);
        }
        else
        {
            _logger.LogDebug("Clock already synced ({MeasurementCount} measurements), skipping burst",
                _clockSynchronizer.GetStatus()?.MeasurementCount ?? 0);
        }

        // Start pipeline immediately - don't block on sync burst
        // The continuous sync loop + sync correction will handle any residual drift
        if (_audioPipeline != null)
        {
            try
            {
                await _audioPipeline.StartAsync(message.Format);

                // Drain any chunks that arrived during initialization
                var drainedCount = 0;
                while (_earlyChunkQueue.TryDequeue(out var chunk))
                {
                    _audioPipeline.ProcessAudioChunk(chunk);
                    drainedCount++;
                }

                if (drainedCount > 0)
                {
                    _logger.LogDebug("Drained {Count} early chunks into pipeline", drainedCount);
                }

                // Infer Playing state from stream/start for servers that don't send group/update
                _currentGroup ??= new GroupState();
                _currentGroup.PlaybackState = PlaybackState.Playing;
                GroupStateChanged?.Invoke(this, _currentGroup);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start audio pipeline");
            }
        }
    }

    private async Task HandleStreamEndAsync(string json)
    {
        var message = MessageSerializer.Deserialize<StreamEndMessage>(json);
        _logger.LogInformation("Stream ended: {Reason}", message?.Reason ?? "unknown");

        // Clear any queued chunks from this stream
        while (_earlyChunkQueue.TryDequeue(out _))
        {
        }

        if (_audioPipeline != null)
        {
            try
            {
                await _audioPipeline.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop audio pipeline");
            }
        }

        // Update playback state to reflect stream ended
        if (_currentGroup != null)
        {
            _currentGroup.PlaybackState = PlaybackState.Idle;
            GroupStateChanged?.Invoke(this, _currentGroup);
        }
    }

    private void HandleStreamClear(string json)
    {
        var message = MessageSerializer.Deserialize<StreamClearMessage>(json);
        _logger.LogDebug("Stream clear (seek)");

        _audioPipeline?.Clear();
    }

    private void OnBinaryMessageReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (!BinaryMessageParser.TryParse(data.Span, out var type, out var timestamp, out var payload))
        {
            _logger.LogWarning("Failed to parse binary message");
            return;
        }

        var category = BinaryMessageParser.GetCategory(type);

        switch (category)
        {
            case BinaryMessageCategory.PlayerAudio:
                var audioChunk = BinaryMessageParser.ParseAudioChunk(data.Span);
                if (audioChunk != null)
                {
                    if (_audioPipeline?.IsReady == true)
                    {
                        // Pipeline ready - process immediately
                        _audioPipeline.ProcessAudioChunk(audioChunk);
                    }
                    else if (_earlyChunkQueue.Count < MaxEarlyChunks)
                    {
                        // Pipeline not ready yet - queue for later processing
                        // This prevents chunk loss during decoder/buffer initialization
                        _earlyChunkQueue.Enqueue(audioChunk);
                        _logger.LogTrace("Queued early chunk ({QueueSize} in queue)", _earlyChunkQueue.Count);
                    }
                    // else: queue full, drop chunk (should rarely happen)
                }

                _logger.LogTrace("Audio chunk: {Length} bytes @ {Timestamp}", payload.Length, timestamp);
                break;

            case BinaryMessageCategory.Artwork:
                var artwork = BinaryMessageParser.ParseArtworkChunk(data.Span);
                if (artwork is not null)
                {
                    _logger.LogDebug("Artwork received: {Length} bytes", artwork.ImageData.Length);
                    ArtworkReceived?.Invoke(this, artwork.ImageData);
                }
                break;

            case BinaryMessageCategory.Visualizer:
                // TODO: Handle visualizer data
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();

        // NOTE: We do NOT dispose _audioPipeline here - it's a shared singleton
        // managed by the DI container. We only stop playback if active.
        if (_audioPipeline != null)
        {
            await _audioPipeline.StopAsync();
        }

        _connection.StateChanged -= OnConnectionStateChanged;
        _connection.TextMessageReceived -= OnTextMessageReceived;
        _connection.BinaryMessageReceived -= OnBinaryMessageReceived;

        await _connection.DisposeAsync();
    }
}
