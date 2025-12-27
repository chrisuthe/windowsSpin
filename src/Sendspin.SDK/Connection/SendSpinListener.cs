using Fleck;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// WebSocket server listener for server-initiated Sendspin connections.
/// Uses Fleck WebSocket server which doesn't require admin privileges.
/// Listens on the configured port and accepts incoming connections from Sendspin servers.
/// </summary>
public sealed class SendspinListener : IAsyncDisposable
{
    private readonly ILogger<SendspinListener> _logger;
    private readonly ListenerOptions _options;
    private WebSocketServer? _server;
    private bool _disposed;
    private bool _isListening;

    /// <summary>
    /// Raised when a new server connects.
    /// </summary>
    public event EventHandler<IWebSocketConnection>? ServerConnected;

    /// <summary>
    /// Whether the listener is currently running.
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// The port the listener is bound to.
    /// </summary>
    public int Port => _options.Port;

    public SendspinListener(ILogger<SendspinListener> logger, ListenerOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ListenerOptions();
    }

    /// <summary>
    /// Starts listening for incoming WebSocket connections.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isListening)
        {
            _logger.LogWarning("Listener is already running");
            return Task.CompletedTask;
        }

        // Construct the WebSocket URL
        var url = $"ws://0.0.0.0:{_options.Port}";

        _server = new WebSocketServer(url);

        // Configure Fleck to use our logger
        FleckLog.LogAction = (level, message, ex) =>
        {
            switch (level)
            {
                case Fleck.LogLevel.Debug:
                    _logger.LogDebug(ex, "[Fleck] {Message}", message);
                    break;
                case Fleck.LogLevel.Info:
                    _logger.LogInformation(ex, "[Fleck] {Message}", message);
                    break;
                case Fleck.LogLevel.Warn:
                    _logger.LogWarning(ex, "[Fleck] {Message}", message);
                    break;
                case Fleck.LogLevel.Error:
                    _logger.LogError(ex, "[Fleck] {Message}", message);
                    break;
            }
        };

        // Start the server
        _server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                _logger.LogInformation("WebSocket connection opened from {ConnectionInfo}",
                    socket.ConnectionInfo.ClientIpAddress);

                // Check if this is the correct path
                var path = socket.ConnectionInfo.Path;
                if (!string.Equals(path, _options.Path, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, _options.Path + "/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Connection to unexpected path: {Path}, expected: {Expected}",
                        path, _options.Path);
                }

                // Raise the event
                ServerConnected?.Invoke(this, socket);
            };

            socket.OnClose = () =>
            {
                _logger.LogDebug("WebSocket connection closed from {ConnectionInfo}",
                    socket.ConnectionInfo.ClientIpAddress);
            };

            socket.OnError = ex =>
            {
                _logger.LogError(ex, "WebSocket error from {ConnectionInfo}",
                    socket.ConnectionInfo.ClientIpAddress);
            };
        });

        _isListening = true;
        _logger.LogInformation("Sendspin listener started on ws://0.0.0.0:{Port} (path: {Path})",
            _options.Port, _options.Path);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops listening for connections.
    /// </summary>
    public Task StopAsync()
    {
        if (_server == null || !_isListening)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping Sendspin listener");

        try
        {
            _server.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping WebSocket server");
        }
        finally
        {
            _server = null;
            _isListening = false;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }
}

/// <summary>
/// Configuration options for the Sendspin listener.
/// </summary>
public sealed class ListenerOptions
{
    /// <summary>
    /// Port to listen on.
    /// Default: 8927 (Sendspin standard port)
    /// </summary>
    public int Port { get; set; } = 8927;

    /// <summary>
    /// WebSocket endpoint path.
    /// Default: "/sendspin"
    /// </summary>
    public string Path { get; set; } = "/sendspin";
}
