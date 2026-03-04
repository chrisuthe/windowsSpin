using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Wraps a System.Net.WebSockets.WebSocket accepted by SimpleWebSocketServer.
/// Provides event-based message dispatch (OnMessage, OnBinary, OnClose, OnError)
/// and send methods, replacing Fleck's IWebSocketConnection.
/// </summary>
public sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>Client IP address.</summary>
    public IPAddress ClientIpAddress { get; }

    /// <summary>Client port.</summary>
    public int ClientPort { get; }

    /// <summary>The HTTP request path used during the WebSocket upgrade.</summary>
    public string Path { get; }

    /// <summary>Raised when a text message is received.</summary>
    public Action<string>? OnMessage { get; set; }

    /// <summary>Raised when a binary message is received.</summary>
    public Action<byte[]>? OnBinary { get; set; }

    /// <summary>Raised when the connection closes.</summary>
    public Action? OnClose { get; set; }

    /// <summary>Raised when an error occurs.</summary>
    public Action<Exception>? OnError { get; set; }

    public WebSocketClientConnection(
        WebSocket webSocket,
        IPAddress clientIpAddress,
        int clientPort,
        string path,
        ILogger? logger = null)
    {
        _webSocket = webSocket;
        ClientIpAddress = clientIpAddress;
        ClientPort = clientPort;
        Path = path;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background receive loop that dispatches messages to callbacks.
    /// </summary>
    public void StartReceiving()
    {
        if (_receiveLoop is not null)
            throw new InvalidOperationException("Receive loop already started");

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends a text message.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a binary message.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        await _webSocket.SendAsync(
            data.AsMemory(),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initiates a graceful WebSocket close.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket.State == WebSocketState.Open ||
            _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "closing",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Error during graceful WebSocket close");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnClose?.Invoke();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var data = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(data);
                    OnMessage?.Invoke(text);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    OnBinary?.Invoke(data);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex) when (
            _webSocket.State == WebSocketState.Aborted ||
            _webSocket.State == WebSocketState.Closed)
        {
            _logger?.LogDebug(ex, "WebSocket closed during receive");
            OnClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket receive error");
            OnError?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (_webSocket.State != WebSocketState.Closed &&
                _webSocket.State != WebSocketState.Aborted)
            {
                OnClose?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _webSocket.Dispose();
        _cts.Dispose();
    }
}
