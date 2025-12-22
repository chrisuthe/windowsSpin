using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Connection;
using SendSpinClient.Core.Models;
using SendSpinClient.Core.Protocol;
using SendSpinClient.Core.Protocol.Messages;
using SendSpinClient.Core.Synchronization;

namespace SendSpinClient.Core.Client;

/// <summary>
/// Main SendSpin client that orchestrates connection, handshake, and message handling.
/// </summary>
public sealed class SendSpinClientService : ISendSpinClient
{
    private readonly ILogger<SendSpinClientService> _logger;
    private readonly ISendSpinConnection _connection;
    private readonly ClientCapabilities _capabilities;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly IAudioPipeline? _audioPipeline;

    private TaskCompletionSource<bool>? _handshakeTcs;
    private GroupState? _currentGroup;
    private CancellationTokenSource? _timeSyncCts;
    private Task? _timeSyncTask;
    private long _lastSentTimestamp;


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

    public SendSpinClientService(
        ILogger<SendSpinClientService> logger,
        ISendSpinConnection connection,
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
        _logger.LogInformation("Connecting to {Uri}", serverUri);

        // Connect WebSocket
        await _connection.ConnectAsync(serverUri, cancellationToken);

        // Prepare handshake completion
        _handshakeTcs = new TaskCompletionSource<bool>();

        // Send client hello with proper payload envelope
        // Support both Opus (preferred for streaming) and PCM
        var hello = ClientHelloMessage.Create(
            clientId: _capabilities.ClientId,
            name: _capabilities.ClientName,
            supportedRoles: _capabilities.Roles,
            playerSupport: new PlayerSupport
            {
                SupportedFormats = new List<AudioFormatSpec>
                {
                    // Opus 48kHz stereo - preferred for streaming (low bandwidth, good quality)
                    new() { Codec = "opus", Channels = 2, SampleRate = 48000 },
                    // PCM fallback
                    new() { Codec = "pcm", Channels = 2, SampleRate = 44100, BitDepth = 16 },
                    new() { Codec = "pcm", Channels = 2, SampleRate = 48000, BitDepth = 16 },
                },
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
        var message = new ClientCommandMessage
        {
            Command = command,
            GroupId = _currentGroup?.GroupId,
            Params = parameters
        };

        _logger.LogDebug("Sending command: {Command}", command);
        await _connection.SendMessageAsync(message);
    }

    public async Task SetVolumeAsync(int volume)
    {
        await SendCommandAsync(Commands.SetVolume, new Dictionary<string, object>
        {
            ["volume"] = Math.Clamp(volume, 0, 100)
        });
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
            _logger.LogInformation("Received message type: {Type}\n{Json}",
                messageType ?? "unknown",
                json.Length > 1000 ? json[..1000] + "..." : json);

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
                    HandleStreamStart(json);
                    break;

                case MessageTypes.StreamEnd:
                    HandleStreamEnd(json);
                    break;

                case MessageTypes.StreamClear:
                    HandleStreamClear(json);
                    break;

                case MessageTypes.ServerState:
                    HandleServerState(json);
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
        if (_connection is SendSpinConnection conn)
        {
            conn.MarkConnected();
        }

        // Reset clock synchronizer for new connection
        _clockSynchronizer.Reset();

        // Send initial client state (required by protocol after server/hello)
        // This tells the server we're synchronized and ready
        _ = SendInitialClientStateAsync();

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
    /// Matches aiosendspin's adaptive interval strategy.
    /// </summary>
    private int GetAdaptiveTimeSyncIntervalMs()
    {
        var status = _clockSynchronizer.GetStatus();

        // If not enough measurements yet, sync rapidly
        if (status.MeasurementCount < 3)
            return 200; // 200ms - aggressive initial sync

        // Uncertainty in milliseconds
        var uncertaintyMs = status.OffsetUncertaintyMicroseconds / 1000.0;

        // Adaptive intervals based on sync quality (matching aiosendspin)
        if (uncertaintyMs < 1.0)
            return 3000;  // Well synchronized: 3s
        else if (uncertaintyMs < 2.0)
            return 1000;  // Good sync: 1s
        else if (uncertaintyMs < 5.0)
            return 500;   // Moderate sync: 500ms
        else
            return 200;   // Poor sync: 200ms
    }

    private async Task TimeSyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _connection.State == ConnectionState.Connected)
            {
                // Send time sync message
                await SendTimeSyncAsync(cancellationToken);

                // Calculate adaptive interval based on current sync quality
                var intervalMs = GetAdaptiveTimeSyncIntervalMs();

                _logger.LogTrace("Next time sync in {Interval}ms (uncertainty: {Uncertainty:F2}ms)",
                    intervalMs,
                    _clockSynchronizer.GetStatus().OffsetUncertaintyMicroseconds / 1000.0);

                // Wait for the interval
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

    private async Task SendTimeSyncAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Connected)
            return;

        try
        {
            var timeMessage = ClientTimeMessage.CreateNow();
            _lastSentTimestamp = timeMessage.ClientTransmitted;

            await _connection.SendMessageAsync(timeMessage, cancellationToken);
            _logger.LogTrace("Sent client/time: T1={T1}", _lastSentTimestamp);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send time sync message");
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

        // Track if we were already converged before this measurement
        bool wasConverged = _clockSynchronizer.IsConverged;

        // Process through Kalman filter
        _clockSynchronizer.ProcessMeasurement(t1, t2, t3, t4);

        // Log the sync status periodically
        var status = _clockSynchronizer.GetStatus();
        if (status.MeasurementCount <= 10 || status.MeasurementCount % 10 == 0)
        {
            _logger.LogInformation(
                "Clock sync: offset={Offset:F2}ms (±{Uncertainty:F2}ms), drift={Drift:F2}μs/s, converged={Converged}",
                status.OffsetMilliseconds,
                status.OffsetUncertaintyMicroseconds / 1000.0,
                status.DriftMicrosecondsPerSecond,
                status.IsConverged);
        }

        // Notify when first converged
        if (!wasConverged && _clockSynchronizer.IsConverged)
        {
            _logger.LogInformation("Clock synchronization converged after {Count} measurements", status.MeasurementCount);
            ClockSyncConverged?.Invoke(this, status);
        }
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

        _logger.LogDebug("Group update: {State}, {Track}",
            _currentGroup.PlaybackState,
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

        // Update controller state (volume, mute)
        if (payload.Controller is not null)
        {
            if (payload.Controller.Volume.HasValue)
                _currentGroup.Volume = payload.Controller.Volume.Value;
            if (payload.Controller.Muted.HasValue)
                _currentGroup.Muted = payload.Controller.Muted.Value;
        }

        _logger.LogDebug("Server state update: {Track} by {Artist}",
            _currentGroup.Metadata?.Title ?? "unknown",
            _currentGroup.Metadata?.Artist ?? "unknown");

        GroupStateChanged?.Invoke(this, _currentGroup);
    }

    private async void HandleStreamStart(string json)
    {
        var message = MessageSerializer.Deserialize<StreamStartMessage>(json);
        if (message is null)
        {
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", message.Format);

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

    private async void HandleStreamEnd(string json)
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
        StopTimeSyncLoop();

        // Stop audio pipeline
        if (_audioPipeline != null)
        {
            await _audioPipeline.DisposeAsync();
        }

        _connection.StateChanged -= OnConnectionStateChanged;
        _connection.TextMessageReceived -= OnTextMessageReceived;
        _connection.BinaryMessageReceived -= OnBinaryMessageReceived;

        await _connection.DisposeAsync();
    }
}
