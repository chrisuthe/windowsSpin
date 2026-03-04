using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Minimal WebSocket server using TcpListener + WebSocket.CreateFromStream().
/// Replaces Fleck for NativeAOT compatibility — no HTTP.sys, no admin privileges.
/// </summary>
public sealed partial class SimpleWebSocketServer : IAsyncDisposable
{
    /// <summary>
    /// The WebSocket GUID used in the Sec-WebSocket-Accept computation per RFC 6455.
    /// </summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-5AB0DC85B11C";

    private readonly ILogger? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>
    /// Port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Raised when a new WebSocket client connects. The handler receives a
    /// <see cref="WebSocketClientConnection"/> with the receive loop already started.
    /// </summary>
    public event EventHandler<WebSocketClientConnection>? ClientConnected;

    public SimpleWebSocketServer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts listening for incoming WebSocket connections.
    /// </summary>
    /// <param name="port">Port to bind to.</param>
    public void Start(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
            throw new InvalidOperationException("Server is already running");

        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _logger?.LogInformation("WebSocket server listening on port {Port}", port);
    }

    /// <summary>
    /// Stops the server and closes all pending accept operations.
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener is null) return;

        _logger?.LogInformation("Stopping WebSocket server");

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Handle each connection concurrently
                _ = HandleConnectionAsync(tcpClient, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error accepting TCP connection");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var remoteEndPoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
        try
        {
            var stream = tcpClient.GetStream();

            // Read the HTTP upgrade request
            var requestBytes = new byte[4096];
            var bytesRead = await stream.ReadAsync(requestBytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                tcpClient.Dispose();
                return;
            }

            var request = Encoding.UTF8.GetString(requestBytes, 0, bytesRead);

            // Parse the request
            var pathMatch = GetRequestLineRegex().Match(request);
            if (!pathMatch.Success)
            {
                _logger?.LogWarning("Invalid HTTP request from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Bad Request", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var path = pathMatch.Groups[1].Value;

            // Extract WebSocket key
            var keyMatch = WebSocketKeyHeaderRegex().Match(request);
            if (!keyMatch.Success)
            {
                _logger?.LogWarning("Missing Sec-WebSocket-Key from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Missing Sec-WebSocket-Key", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var webSocketKey = keyMatch.Groups[1].Value;

            // Compute Sec-WebSocket-Accept per RFC 6455
            var acceptKey = ComputeAcceptKey(webSocketKey);

            // Send HTTP 101 Switching Protocols
            var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                           $"Upgrade: websocket\r\n" +
                           $"Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                           $"\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            // Create WebSocket from the stream
            var webSocket = WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions { IsServer = true });

            var clientIp = remoteEndPoint?.Address ?? IPAddress.Loopback;
            var clientPort = remoteEndPoint?.Port ?? 0;

            var connection = new WebSocketClientConnection(
                webSocket,
                clientIp,
                clientPort,
                path,
                _logger);

            connection.StartReceiving();

            _logger?.LogDebug("WebSocket connection established from {Endpoint} on path {Path}",
                remoteEndPoint, path);

            ClientConnected?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling WebSocket upgrade from {Endpoint}", remoteEndPoint);
            tcpClient.Dispose();
        }
    }

    private static string ComputeAcceptKey(string webSocketKey)
    {
        var combined = webSocketKey + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static async Task SendHttpResponse(
        NetworkStream stream, int statusCode, string reason,
        CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^GET\s+(\S+)\s+HTTP/1\.1", RegexOptions.Compiled)]
    private static partial Regex GetRequestLineRegex();

    [GeneratedRegex(@"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WebSocketKeyHeaderRegex();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
