using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Protocol;
using SendSpinClient.Core.Protocol.Messages;

namespace SendSpinClient.Core.Connection;

/// <summary>
/// WebSocket connection to a SendSpin server.
/// Handles connection lifecycle, message sending/receiving, and automatic reconnection.
/// </summary>
public sealed class SendSpinConnection : ISendSpinConnection
{
    private readonly ILogger<SendSpinConnection> _logger;
    private readonly ConnectionOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Uri? _serverUri;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _reconnectAttempt;
    private bool _disposed;

    public ConnectionState State => _state;
    public Uri? ServerUri => _serverUri;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public SendSpinConnection(ILogger<SendSpinConnection> logger, ConnectionOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ConnectionOptions();
    }

    public async Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state is ConnectionState.Connected or ConnectionState.Connecting)
        {
            throw new InvalidOperationException($"Cannot connect while in state {_state}");
        }

        _serverUri = serverUri;
        _reconnectAttempt = 0;

        await ConnectInternalAsync(cancellationToken);
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        if (_serverUri is null)
            throw new InvalidOperationException("Server URI not set");

        SetState(ConnectionState.Connecting);

        try
        {
            // Clean up previous connection
            await CleanupWebSocketAsync();

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(_options.KeepAliveIntervalMs);

            using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            _logger.LogInformation("Connecting to {Uri}...", _serverUri);
            await _webSocket.ConnectAsync(_serverUri, linkedCts.Token);

            _logger.LogInformation("Connected to {Uri}", _serverUri);
            _reconnectAttempt = 0;

            // Start receive loop
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            SetState(ConnectionState.Handshaking);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Disconnected, "Connection cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to {Uri}", _serverUri);
            SetState(ConnectionState.Disconnected, ex.Message, ex);

            if (_options.AutoReconnect && !cancellationToken.IsCancellationRequested)
            {
                await TryReconnectAsync(cancellationToken);
            }
            else
            {
                throw;
            }
        }
    }

    public async Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Disconnected)
            return;

        SetState(ConnectionState.Disconnecting, reason);

        try
        {
            // Send goodbye message if connected
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var goodbye = new ClientGoodbyeMessage { Reason = reason };
                    await SendMessageAsync(goodbye, cancellationToken);

                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during graceful disconnect");
                }
            }
        }
        finally
        {
            await CleanupWebSocketAsync();
            SetState(ConnectionState.Disconnected, reason);
        }
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var json = MessageSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogDebug("Sending: {Message}", json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(
                data,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferSize);
        var messageBuffer = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                messageBuffer.SetLength(0);

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Server closed connection: {Status} - {Description}",
                            result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var messageData = messageBuffer.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(messageData);
                    _logger.LogDebug("Received text: {Message}", text.Length > 500 ? text[..500] + "..." : text);
                    TextMessageReceived?.Invoke(this, text);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    _logger.LogTrace("Received binary: {Length} bytes", messageData.Length);
                    BinaryMessageReceived?.Invoke(this, messageData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("Connection closed unexpectedly");
            await HandleConnectionLostAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in receive loop");
            await HandleConnectionLostAsync();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            messageBuffer.Dispose();
        }
    }

    private async Task HandleConnectionLostAsync()
    {
        if (_state == ConnectionState.Disconnecting || _disposed)
            return;

        SetState(ConnectionState.Reconnecting, "Connection lost");

        if (_options.AutoReconnect)
        {
            await TryReconnectAsync(CancellationToken.None);
        }
        else
        {
            SetState(ConnectionState.Disconnected, "Connection lost");
        }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            if (_options.MaxReconnectAttempts >= 0 && _reconnectAttempt >= _options.MaxReconnectAttempts)
            {
                _logger.LogWarning("Max reconnection attempts ({Max}) reached", _options.MaxReconnectAttempts);
                SetState(ConnectionState.Disconnected, "Max reconnection attempts reached");
                return;
            }

            _reconnectAttempt++;
            var delay = CalculateReconnectDelay();

            _logger.LogInformation("Reconnecting in {Delay}ms (attempt {Attempt})...", delay, _reconnectAttempt);
            SetState(ConnectionState.Reconnecting, $"Attempt {_reconnectAttempt}");

            try
            {
                await Task.Delay(delay, cancellationToken);
                await ConnectInternalAsync(cancellationToken);

                if (_state == ConnectionState.Handshaking || _state == ConnectionState.Connected)
                {
                    return; // Successfully reconnected
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnection attempt {Attempt} failed", _reconnectAttempt);
            }
        }
    }

    private int CalculateReconnectDelay()
    {
        var delay = (int)(_options.ReconnectDelayMs * Math.Pow(_options.ReconnectBackoffMultiplier, _reconnectAttempt - 1));
        return Math.Min(delay, _options.MaxReconnectDelayMs);
    }

    private async Task CleanupWebSocketAsync()
    {
        _receiveCts?.Cancel();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { /* Ignore timeout */ }
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;

        if (_webSocket is not null)
        {
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    private void SetState(ConnectionState newState, string? reason = null, Exception? exception = null)
    {
        var oldState = _state;
        if (oldState == newState) return;

        _state = newState;
        _logger.LogDebug("Connection state: {OldState} -> {NewState} ({Reason})",
            oldState, newState, reason ?? "N/A");

        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldState = oldState,
            NewState = newState,
            Reason = reason,
            Exception = exception
        });
    }

    /// <summary>
    /// Marks the connection as fully connected (called after handshake).
    /// </summary>
    public void MarkConnected()
    {
        if (_state == ConnectionState.Handshaking)
        {
            SetState(ConnectionState.Connected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync("disposing");
        _sendLock.Dispose();
    }
}
