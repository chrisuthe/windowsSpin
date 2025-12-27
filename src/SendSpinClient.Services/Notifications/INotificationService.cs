using SendSpin.SDK.Models;

namespace SendSpinClient.Services.Notifications;

/// <summary>
/// Service interface for displaying system notifications to the user.
/// </summary>
/// <remarks>
/// <para>
/// This interface abstracts the notification mechanism, allowing different implementations
/// for various platforms (Windows Toast, in-app notifications, etc.) while maintaining
/// a consistent API for the application layer.
/// </para>
/// <para>
/// The service is designed to be non-blocking - all notification methods should return
/// quickly without waiting for user interaction.
/// </para>
/// </remarks>
public interface INotificationService : IDisposable
{
    /// <summary>
    /// Gets or sets whether notifications are enabled globally.
    /// </summary>
    /// <remarks>
    /// When disabled, all notification methods become no-ops.
    /// This allows users to silence notifications without unregistering the service.
    /// </remarks>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the current notification settings.
    /// </summary>
    NotificationSettings Settings { get; set; }

    /// <summary>
    /// Displays a notification when a new track starts playing.
    /// </summary>
    /// <param name="track">The metadata of the track that started playing.</param>
    /// <param name="albumArtwork">Optional album artwork as a byte array (PNG/JPEG).</param>
    /// <param name="artworkUrl">Optional URL to fetch artwork from (preferred over byte array for freshness).</param>
    /// <remarks>
    /// <para>
    /// This notification is typically shown when:
    /// - A new song begins playback
    /// - The track changes during continuous playback
    /// - Playback resumes with a different track than when paused
    /// </para>
    /// <para>
    /// The notification will include track title, artist, and album artwork if available.
    /// If <paramref name="artworkUrl"/> is provided, it will be used directly (Windows fetches it).
    /// Otherwise, <paramref name="albumArtwork"/> bytes will be saved to a temp file.
    /// If neither is available, no artwork is shown.
    /// </para>
    /// </remarks>
    void ShowTrackChanged(TrackMetadata track, byte[]? albumArtwork = null, string? artworkUrl = null);

    /// <summary>
    /// Displays a notification when playback state changes.
    /// </summary>
    /// <param name="newState">The new playback state.</param>
    /// <param name="track">The current track metadata (may be null if idle/stopped).</param>
    /// <remarks>
    /// <para>
    /// Use this for state transitions like:
    /// - Playing → Paused
    /// - Paused → Playing (resume)
    /// - Playing → Stopped
    /// </para>
    /// <para>
    /// Note: Track changes should use <see cref="ShowTrackChanged"/> instead,
    /// as they provide richer notification content.
    /// </para>
    /// </remarks>
    void ShowPlaybackStateChanged(PlaybackState newState, TrackMetadata? track = null);

    /// <summary>
    /// Displays a notification when the client connects to a server.
    /// </summary>
    /// <param name="serverName">The display name of the connected server.</param>
    void ShowConnected(string serverName);

    /// <summary>
    /// Displays a notification when the client disconnects from a server.
    /// </summary>
    /// <param name="serverName">The display name of the server that was disconnected.</param>
    /// <param name="reason">Optional reason for disconnection (e.g., "Server offline", "Network error").</param>
    void ShowDisconnected(string serverName, string? reason = null);

    /// <summary>
    /// Displays a generic informational notification.
    /// </summary>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification body text.</param>
    void ShowInfo(string title, string message);

    /// <summary>
    /// Displays an error notification.
    /// </summary>
    /// <param name="title">The error title.</param>
    /// <param name="message">The error details.</param>
    void ShowError(string title, string message);

    /// <summary>
    /// Clears all pending notifications from the notification center/action center.
    /// </summary>
    /// <remarks>
    /// This is useful when the application exits or when the user wants to
    /// dismiss all SendSpin notifications at once.
    /// </remarks>
    void ClearAll();
}
