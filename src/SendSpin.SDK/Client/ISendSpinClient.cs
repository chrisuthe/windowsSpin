using SendSpin.SDK.Connection;
using SendSpin.SDK.Models;
using SendSpin.SDK.Synchronization;

namespace SendSpin.SDK.Client;

/// <summary>
/// Main client interface for interacting with a SendSpin server.
/// </summary>
public interface ISendSpinClient : IAsyncDisposable
{
    /// <summary>
    /// Current connection state.
    /// </summary>
    ConnectionState ConnectionState { get; }

    /// <summary>
    /// Server ID after successful connection.
    /// </summary>
    string? ServerId { get; }

    /// <summary>
    /// Server name after successful connection.
    /// </summary>
    string? ServerName { get; }

    /// <summary>
    /// Current group state.
    /// </summary>
    GroupState? CurrentGroup { get; }

    /// <summary>
    /// Current clock synchronization status.
    /// </summary>
    ClockSyncStatus? ClockSyncStatus { get; }

    /// <summary>
    /// Whether the clock synchronizer has converged to a stable estimate.
    /// </summary>
    bool IsClockSynced { get; }

    /// <summary>
    /// Connects to a SendSpin server.
    /// </summary>
    Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    Task DisconnectAsync(string reason = "user_request");

    /// <summary>
    /// Sends a playback command.
    /// </summary>
    Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null);

    /// <summary>
    /// Sets the volume level (0-100).
    /// </summary>
    Task SetVolumeAsync(int volume);

    /// <summary>
    /// Clears the audio buffer, causing the pipeline to restart buffering.
    /// Use this when audio sync parameters change and you want immediate effect.
    /// </summary>
    void ClearAudioBuffer();

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when group state updates (playback, metadata, volume).
    /// </summary>
    event EventHandler<GroupState>? GroupStateChanged;

    /// <summary>
    /// Event raised when artwork is received.
    /// </summary>
    event EventHandler<byte[]>? ArtworkReceived;
}
