namespace SendspinClient.Services.Notifications;

/// <summary>
/// Configuration settings for the notification service.
/// </summary>
/// <remarks>
/// <para>
/// These settings allow users to customize which events trigger notifications
/// and how those notifications behave. All settings are persisted to user preferences.
/// </para>
/// <para>
/// Default values are chosen to provide a good out-of-box experience without
/// being overly intrusive - track changes are shown, but play/pause events are not.
/// </para>
/// </remarks>
public sealed class NotificationSettings
{
    /// <summary>
    /// Gets or sets whether notifications are enabled globally.
    /// </summary>
    /// <remarks>
    /// When false, no notifications will be shown regardless of other settings.
    /// This is the master switch for the notification system.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to show notifications when a new track starts playing.
    /// </summary>
    /// <remarks>
    /// This is the most common notification type for media players.
    /// Shows the track title, artist, album, and artwork when available.
    /// </remarks>
    public bool ShowOnTrackChange { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to show notifications when playback starts.
    /// </summary>
    /// <remarks>
    /// When true, shows a notification when playback begins (Play button pressed).
    /// This is disabled by default to avoid notification spam when frequently
    /// toggling play/pause.
    /// </remarks>
    public bool ShowOnPlaybackStart { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to show notifications when playback is paused.
    /// </summary>
    /// <remarks>
    /// When true, shows a "Paused" notification.
    /// Disabled by default as pause events are usually intentional user actions.
    /// </remarks>
    public bool ShowOnPlaybackPause { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to show notifications when playback stops.
    /// </summary>
    /// <remarks>
    /// When true, shows a notification when playback ends (queue finished or stopped).
    /// Useful for users who want confirmation that playback has completed.
    /// </remarks>
    public bool ShowOnPlaybackStop { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to show notifications when connecting to a server.
    /// </summary>
    /// <remarks>
    /// Shows the server name when a connection is established.
    /// Useful for multi-server setups or when auto-connect is enabled.
    /// </remarks>
    public bool ShowOnConnect { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to show notifications when disconnecting from a server.
    /// </summary>
    /// <remarks>
    /// Shows a notification when connection is lost, either intentionally
    /// or due to network issues. Includes the disconnect reason when available.
    /// </remarks>
    public bool ShowOnDisconnect { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to show album artwork in track notifications.
    /// </summary>
    /// <remarks>
    /// When true, notifications will include album art if available.
    /// Disabled by default for reliability - artwork fetching can cause
    /// timing issues and Windows Toast can fail silently with remote images.
    /// </remarks>
    public bool ShowAlbumArtwork { get; set; } = false;

    /// <summary>
    /// Gets or sets the notification duration in seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a hint to the notification system - actual duration may vary
    /// based on Windows settings and notification priority.
    /// </para>
    /// <para>
    /// Values:
    /// - 0: Use system default (typically 5 seconds)
    /// - 1-25: Custom duration in seconds
    /// </para>
    /// <para>
    /// Note: Windows Toast notifications have limited duration control.
    /// The "Short" duration is about 5 seconds, "Long" is about 25 seconds.
    /// </para>
    /// </remarks>
    public int DurationSeconds { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether notifications should play a sound.
    /// </summary>
    /// <remarks>
    /// When false, notifications will be silent (no audio cue).
    /// This respects Windows Focus Assist settings regardless of this value.
    /// </remarks>
    public bool PlaySound { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to suppress notifications when the main window is visible.
    /// </summary>
    /// <remarks>
    /// When true, notifications are only shown when the app is minimized to tray
    /// or the window is not focused. This prevents redundant notifications when
    /// the user is already looking at the app.
    /// </remarks>
    public bool OnlyWhenMinimized { get; set; } = true;

    /// <summary>
    /// Creates a new instance with default settings.
    /// </summary>
    /// <returns>A new <see cref="NotificationSettings"/> with default values.</returns>
    public static NotificationSettings Default => new();

    /// <summary>
    /// Creates a copy of the current settings.
    /// </summary>
    /// <returns>A new <see cref="NotificationSettings"/> instance with the same values.</returns>
    public NotificationSettings Clone()
    {
        return new NotificationSettings
        {
            Enabled = Enabled,
            ShowOnTrackChange = ShowOnTrackChange,
            ShowOnPlaybackStart = ShowOnPlaybackStart,
            ShowOnPlaybackPause = ShowOnPlaybackPause,
            ShowOnPlaybackStop = ShowOnPlaybackStop,
            ShowOnConnect = ShowOnConnect,
            ShowOnDisconnect = ShowOnDisconnect,
            ShowAlbumArtwork = ShowAlbumArtwork,
            DurationSeconds = DurationSeconds,
            PlaySound = PlaySound,
            OnlyWhenMinimized = OnlyWhenMinimized
        };
    }
}
