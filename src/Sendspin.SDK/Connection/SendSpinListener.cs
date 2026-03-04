using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// WebSocket server listener for server-initiated Sendspin connections.
/// Uses a built-in .NET WebSocket server (no admin privileges required).
/// Listens on the configured port and accepts incoming connections from Sendspin servers.
/// </summary>
public sealed class SendspinListener : IAsyncDisposable
{
    private readonly ILogger<SendspinListener> _logger;
    private readonly ListenerOptions _options;
    private SimpleWebSocketServer? _server;
    private bool _disposed;
    private bool _isListening;

    /// <summary>
    /// Raised when a new server connects.
    /// </summary>
    public event EventHandler<WebSocketClientConnection>? ServerConnected;

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

        _server = new SimpleWebSocketServer(_logger);
        _server.ClientConnected += OnClientConnected;
        _server.Start(_options.Port);

        _isListening = true;
        _logger.LogInformation("Sendspin listener started on ws://0.0.0.0:{Port} (path: {Path})",
            _options.Port, _options.Path);

        return Task.CompletedTask;
    }

    private void OnClientConnected(object? sender, WebSocketClientConnection connection)
    {
        _logger.LogInformation("WebSocket connection opened from {ClientIp}",
            connection.ClientIpAddress);

        // Check if this is the correct path
        var path = connection.Path;
        if (!string.Equals(path, _options.Path, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, _options.Path + "/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Connection to unexpected path: {Path}, expected: {Expected}",
                path, _options.Path);
        }

        // Raise the event
        ServerConnected?.Invoke(this, connection);
    }

    /// <summary>
    /// Stops listening for connections.
    /// </summary>
    public async Task StopAsync()
    {
        if (_server == null || !_isListening)
            return;

        _logger.LogInformation("Stopping Sendspin listener");

        try
        {
            await _server.StopAsync();
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
    /// Default: 8928 (Sendspin standard port for clients)
    /// </summary>
    public int Port { get; set; } = 8928;

    /// <summary>
    /// WebSocket endpoint path.
    /// Default: "/sendspin"
    /// </summary>
    public string Path { get; set; } = "/sendspin";
}
