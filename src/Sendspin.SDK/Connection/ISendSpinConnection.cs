using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Interface for the Sendspin WebSocket connection.
/// </summary>
public interface ISendspinConnection : IAsyncDisposable
{
    /// <summary>
    /// Current connection state.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// The URI of the currently connected server.
    /// </summary>
    Uri? ServerUri { get; }

    /// <summary>
    /// Connects to a Sendspin server.
    /// </summary>
    /// <param name="serverUri">WebSocket URI (e.g., ws://host:port/sendspin)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <param name="reason">Reason for disconnection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a JSON protocol message.
    /// </summary>
    Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage;

    /// <summary>
    /// Sends raw binary data.
    /// </summary>
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Event raised when a text (JSON) message is received.
    /// </summary>
    event EventHandler<string>? TextMessageReceived;

    /// <summary>
    /// Event raised when a binary message is received.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;
}
