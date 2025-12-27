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
    private CancellationTokenSource? _timeSyncCts;
    private Task? _timeSyncTask;
    private bool _disposed;

    // Burst sync configuration - send multiple messages and use smallest RTT
    private const int BurstSize = 8;
    private const int BurstIntervalMs = 50; // 50ms between burst messages
    private readonly object _burstLock = new();
    private readonly List<(long t1, long t2, long t3, long t4, double rtt)> _burstResults = new();
    private readonly HashSet<long> _pendingBurstTimestamps = new();

    public ConnectionState ConnectionState => _connection.State;
    public string? ServerId { get; private set; }
    public string? ServerName { get; private set; }
    public GroupState? CurrentGroup => _currentGroup;
    public ClockSyncStatus? ClockSyncStatus => _clockSynchronizer.GetStatus();
    public bool IsClockSynced => _clockSynchronizer.IsConverged;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<GroupState>? GroupStateChanged;
    public event EventHandler<byte[]>? ArtworkReceived;
    public event EventHandler<ClockSyncStatus>? ClockSyncConverged;

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

        // Prepare handshake completion
        _handshakeTcs = new TaskCompletionSource<bool>();

        // Send client hello with proper payload envelope
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
            artworkSupport: null, // Not requesting artwork role
            deviceInfo: new DeviceInfo()
        );

        // Log the full JSON being sent for debugging
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

    public async Task DisconnectAsync(string reason = "user_request")
    {
        if (_disposed) return;

        _logger.LogInformation("Disconnecting: {Reason}", reason);

        // Stop time sync loop
        StopTimeSyncLoop();

        await _connection.DisconnectAsync(reason);

        ServerId = null;
        ServerName = null;
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

        // Handle disconnection
        if (e.NewState == ConnectionState.Disconnected)
        {
            _handshakeTcs?.TrySetResult(false);
            StopTimeSyncLoop();
            ServerId = null;
            ServerName = null;
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

        _logger.LogInformation("Server hello received: {ServerId} ({ServerName}), roles: {Roles}",
            message.ServerId, message.Name, string.Join(", ", message.ActiveRoles));

        // Mark connection as fully connected
        if (_connection is SendspinConnection conn)
        {
            conn.MarkConnected();
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
    /// </summary>
    private async Task SendInitialClientStateAsync()
    {
        try
        {
            var stateMessage = ClientStateMessage.CreateSynchronized(volume: 100, muted: false);
            var stateJson = MessageSerializer.Serialize(stateMessage);
            _logger.LogInformation("Sending initial client/state:\n{Json}", stateJson);
            await _connection.SendMessageAsync(stateMessage);
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
        _timeSyncTask = TimeSyncLoopAsync(_timeSyncCts.Token);

        _logger.LogDebug("Time sync loop started (adaptive intervals)");
    }

    private void StopTimeSyncLoop()
    {
        _timeSyncCts?.Cancel();
        _timeSyncCts?.Dispose();
        _timeSyncCts = null;
        _timeSyncTask = null;
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

        // Update or create group state
        _currentGroup ??= new GroupState { GroupId = message.GroupId };

        if (message.PlaybackState.HasValue)
            _currentGroup.PlaybackState = message.PlaybackState.Value;
        if (message.Volume.HasValue)
        {
            _currentGroup.Volume = message.Volume.Value;
            _audioPipeline?.SetVolume(message.Volume.Value);
        }

        if (message.Muted.HasValue)
        {
            _currentGroup.Muted = message.Muted.Value;
            _audioPipeline?.SetMuted(message.Muted.Value);
        }
        if (message.Metadata is not null)
            _currentGroup.Metadata = message.Metadata;
        if (message.Shuffle.HasValue)
            _currentGroup.Shuffle = message.Shuffle.Value;
        if (message.Repeat is not null)
            _currentGroup.Repeat = message.Repeat;

        _logger.LogDebug("Group update: {State}, Volume={Volume}, Muted={Muted}, Track={Track}",
            _currentGroup.PlaybackState,
            _currentGroup.Volume,
            _currentGroup.Muted,
            _currentGroup.Metadata?.ToString() ?? "no track");

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
                Title = meta.Title ?? existing.Title,
                Artist = meta.Artist ?? existing.Artist,
                Album = meta.Album ?? existing.Album,
                ArtworkUrl = meta.ArtworkUrl ?? existing.ArtworkUrl,
                ArtworkUri = existing.ArtworkUri,
                Duration = meta.Progress?.TrackDuration ?? existing.Duration,
                Position = meta.Progress?.TrackProgress ?? existing.Position
            };

            // Update shuffle/repeat from metadata
            if (meta.Shuffle.HasValue)
                _currentGroup.Shuffle = meta.Shuffle.Value;
            if (meta.Repeat is not null)
                _currentGroup.Repeat = meta.Repeat;
        }

        // Update controller state (volume, mute) and apply to audio pipeline
        if (payload.Controller is not null)
        {
            if (payload.Controller.Volume.HasValue)
            {
                _currentGroup.Volume = payload.Controller.Volume.Value;
                _audioPipeline?.SetVolume(payload.Controller.Volume.Value);
            }
            if (payload.Controller.Muted.HasValue)
            {
                _currentGroup.Muted = payload.Controller.Muted.Value;
                _audioPipeline?.SetMuted(payload.Controller.Muted.Value);
            }
        }

        _logger.LogDebug("Server state update: Volume={Volume}, Muted={Muted}, Track={Track} by {Artist}",
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
    private void HandleServerCommand(string json)
    {
        var message = MessageSerializer.Deserialize<ServerCommandMessage>(json);
        if (message?.Payload?.Player is null)
        {
            _logger.LogDebug("server/command: No player command in message");
            return;
        }

        var player = message.Payload.Player;
        _currentGroup ??= new GroupState();

        _logger.LogDebug("server/command: {Command}", player.Command);

        // Apply volume change - update both state tracking and audio pipeline
        if (player.Volume.HasValue)
        {
            _currentGroup.Volume = player.Volume.Value;
            _audioPipeline?.SetVolume(player.Volume.Value);
        }

        // Apply mute change - update both state tracking and audio pipeline
        if (player.Mute.HasValue)
        {
            _currentGroup.Muted = player.Mute.Value;
            _audioPipeline?.SetMuted(player.Mute.Value);
        }

        // Notify UI of state change
        GroupStateChanged?.Invoke(this, _currentGroup);
    }

    private async Task HandleStreamStartAsync(string json)
    {
        var message = MessageSerializer.Deserialize<StreamStartMessage>(json);
        if (message is null)
        {
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", message.Format);

        // Re-sync clock before starting playback to ensure accurate timing
        // This is critical after pause/resume where the sync may have drifted
        _logger.LogDebug("Triggering clock re-sync burst before stream start");
        await SendTimeSyncBurstAsync(CancellationToken.None);

        if (_audioPipeline != null)
        {
            try
            {
                await _audioPipeline.StartAsync(message.Format, message.TargetTimestamp);
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
    }

    private void HandleStreamClear(string json)
    {
        var message = MessageSerializer.Deserialize<StreamClearMessage>(json);
        _logger.LogDebug("Stream clear (seek)");

        _audioPipeline?.Clear(message?.TargetTimestamp);
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
                if (audioChunk != null && _audioPipeline != null)
                {
                    _audioPipeline.ProcessAudioChunk(audioChunk);
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
