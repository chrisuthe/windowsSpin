using System.Text;
using Fleck;
using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Protocol;
using SendSpinClient.Core.Protocol.Messages;

namespace SendSpinClient.Core.Connection;

/// <summary>
/// Wraps an incoming WebSocket connection from a SendSpin server (using Fleck).
/// Used for server-initiated connections where the server connects to us.
/// </summary>
public sealed class IncomingConnection : ISendSpinConnection
{
    private readonly ILogger<IncomingConnection> _logger;
    private readonly IWebSocketConnection _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;
    private bool _isOpen;

    public ConnectionState State => _state;
    public Uri? ServerUri { get; private set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public IncomingConnection(
        ILogger<IncomingConnection> logger,
        IWebSocketConnection socket)
    {
        _logger = logger;
        _socket = socket;

        // Get server address from connection info
        var clientIp = socket.ConnectionInfo.ClientIpAddress;
        var clientPort = socket.ConnectionInfo.ClientPort;
        ServerUri = new Uri($"ws://{clientIp}:{clientPort}");

        // Wire up Fleck events
        _socket.OnMessage = OnTextMessage;
        _socket.OnBinary = OnBinaryMessage;
        _socket.OnClose = OnClose;
        _socket.OnError = OnError;
    }

    /// <summary>
    /// Starts processing messages on this connection.
    /// For Fleck connections, this just marks the connection as ready.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != ConnectionState.Disconnected)
        {
            throw new InvalidOperationException($"Cannot start while in state {_state}");
        }

        _isOpen = true;
        SetState(ConnectionState.Handshaking);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Not used for incoming connections - throws InvalidOperationException.
    /// </summary>
    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "IncomingConnection does not support outgoing connections. " +
            "Use SendSpinConnection for client-initiated connections.");
    }

    public async Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Disconnected || !_isOpen)
            return;

        SetState(ConnectionState.Disconnecting, reason);

        try
        {
            if (_isOpen)
            {
                try
                {
                    var goodbye = new ClientGoodbyeMessage { Reason = reason };
                    await SendMessageAsync(goodbye, cancellationToken);

                    _socket.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during graceful disconnect");
                }
            }
        }
        finally
        {
            _isOpen = false;
            SetState(ConnectionState.Disconnected, reason);
        }
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isOpen)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var json = MessageSerializer.Serialize(message);

        _sendLock.Wait(cancellationToken);
        try
        {
            _logger.LogDebug("Sending: {Message}", json);
            _socket.Send(json);
        }
        finally
        {
            _sendLock.Release();
        }

        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isOpen)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        _sendLock.Wait(cancellationToken);
        try
        {
            _socket.Send(data.ToArray());
        }
        finally
        {
            _sendLock.Release();
        }

        return Task.CompletedTask;
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

    private void OnTextMessage(string message)
    {
        _logger.LogDebug("Received text: {Message}", message.Length > 500 ? message[..500] + "..." : message);
        TextMessageReceived?.Invoke(this, message);
    }

    private void OnBinaryMessage(byte[] data)
    {
        _logger.LogTrace("Received binary: {Length} bytes", data.Length);
        BinaryMessageReceived?.Invoke(this, data);
    }

    private void OnClose()
    {
        _logger.LogInformation("Server closed connection");
        _isOpen = false;
        SetState(ConnectionState.Disconnected, "Connection closed by server");
    }

    private void OnError(Exception ex)
    {
        _logger.LogError(ex, "WebSocket error");
        _isOpen = false;
        SetState(ConnectionState.Disconnected, ex.Message, ex);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync("disposing");
        _sendLock.Dispose();
    }
}
