using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Sendspin.SDK.Models;

namespace SendspinClient.Services.Notifications;

/// <summary>
/// Windows Toast notification service implementation using the Windows Community Toolkit.
/// </summary>
/// <remarks>
/// <para>
/// This service uses the Microsoft.Toolkit.Uwp.Notifications package to display native
/// Windows 10/11 toast notifications. These notifications appear in the system notification
/// area and are added to the Windows Action Center.
/// </para>
/// <para>
/// <b>Key Features:</b>
/// <list type="bullet">
///   <item>Native Windows 10/11 appearance</item>
///   <item>Album artwork support via temporary files</item>
///   <item>Respects Windows Focus Assist settings</item>
///   <item>Integration with Windows Action Center</item>
///   <item>Configurable notification triggers and behavior</item>
/// </list>
/// </para>
/// <para>
/// <b>Threading:</b> All public methods are thread-safe and can be called from any thread.
/// Toast notifications are marshaled to the appropriate thread internally.
/// </para>
/// <para>
/// <b>Temporary Files:</b> Album artwork is written to temporary files because Windows Toast
/// notifications require a file URI for images. These files are cleaned up periodically
/// and when the service is disposed.
/// </para>
/// </remarks>
public sealed class WindowsToastNotificationService : INotificationService
{
    private readonly ILogger<WindowsToastNotificationService> _logger;
    private readonly Func<bool>? _isWindowVisibleCallback;
    private readonly string _tempArtworkDirectory;
    private readonly object _lock = new();
    private NotificationSettings _settings;
    private bool _disposed;

    /// <summary>
    /// The application ID used for toast notifications.
    /// </summary>
    /// <remarks>
    /// This ID groups all notifications from this application together
    /// and allows Windows to manage them as a unit.
    /// </remarks>
    private const string AppId = "SendspinClient";

    /// <summary>
    /// Tag used for track change notifications, allowing replacement of previous track notifications.
    /// </summary>
    private const string TrackNotificationTag = "track";

    /// <summary>
    /// Tag used for state change notifications.
    /// </summary>
    private const string StateNotificationTag = "state";

    /// <summary>
    /// Tag used for connection notifications.
    /// </summary>
    private const string ConnectionNotificationTag = "connection";

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsToastNotificationService"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="isWindowVisibleCallback">
    /// Optional callback to check if the main window is visible.
    /// When provided and <see cref="NotificationSettings.OnlyWhenMinimized"/> is true,
    /// notifications are suppressed when the window is visible.
    /// </param>
    /// <remarks>
    /// The callback is invoked on the calling thread, so it should be thread-safe
    /// or marshal to the UI thread internally if needed.
    /// </remarks>
    public WindowsToastNotificationService(
        ILogger<WindowsToastNotificationService> logger,
        Func<bool>? isWindowVisibleCallback = null)
    {
        _logger = logger;
        _isWindowVisibleCallback = isWindowVisibleCallback;
        _settings = NotificationSettings.Default;

        // Create a dedicated directory for temporary artwork files
        // Using a subdirectory helps with cleanup and avoids conflicts
        _tempArtworkDirectory = Path.Combine(Path.GetTempPath(), "Sendspin", "artwork");

        try
        {
            Directory.CreateDirectory(_tempArtworkDirectory);
            _logger.LogDebug("Notification artwork directory: {Path}", _tempArtworkDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create artwork temp directory, artwork will not be shown");
        }

        _logger.LogInformation("Windows Toast notification service initialized");
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _settings.Enabled;
            }
        }
        set
        {
            lock (_lock)
            {
                _settings.Enabled = value;
            }
            _logger.LogDebug("Notifications enabled: {Enabled}", value);
        }
    }

    /// <inheritdoc/>
    public NotificationSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings.Clone();
            }
        }
        set
        {
            lock (_lock)
            {
                _settings = value?.Clone() ?? NotificationSettings.Default;
            }
            _logger.LogDebug("Notification settings updated");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// This method creates a rich toast notification with:
    /// <list type="bullet">
    ///   <item>"Now Playing" header</item>
    ///   <item>Track title as the primary text</item>
    ///   <item>Artist and album as secondary text</item>
    ///   <item>Album artwork as hero image (if available and enabled)</item>
    /// </list>
    /// </para>
    /// <para>
    /// The notification uses the "track" tag, so new track notifications will
    /// replace any existing track notification rather than stacking.
    /// </para>
    /// <para>
    /// <b>Artwork Priority:</b> If <paramref name="artworkUrl"/> is provided and valid,
    /// it's used directly (Windows fetches it, ensuring fresh artwork for new tracks).
    /// Otherwise, falls back to <paramref name="albumArtwork"/> bytes.
    /// </para>
    /// </remarks>
    public void ShowTrackChanged(TrackMetadata track, byte[]? albumArtwork = null, string? artworkUrl = null)
    {
        if (!ShouldShowNotification(nameof(Settings.ShowOnTrackChange), () => Settings.ShowOnTrackChange))
            return;

        if (track == null)
        {
            _logger.LogDebug("Skipping track notification - no track metadata");
            return;
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddText("Now Playing")
                .AddText(track.Title ?? "Unknown Track");

            // Build artist/album line
            var secondaryLine = BuildSecondaryLine(track.Artist, track.Album);
            if (!string.IsNullOrEmpty(secondaryLine))
            {
                builder.AddText(secondaryLine);
            }

            // Add album artwork if enabled
            // Note: Remote URLs can cause Windows Toast to fail silently, so we prefer local files
            // Priority: 1) albumArtwork bytes (reliable), 2) skip artwork (still show notification)
            if (Settings.ShowAlbumArtwork && albumArtwork != null)
            {
                var artworkPath = SaveArtworkToTemp(albumArtwork);
                if (!string.IsNullOrEmpty(artworkPath))
                {
                    // Use hero image for a more prominent display
                    builder.AddHeroImage(new Uri(artworkPath));
                    _logger.LogTrace("Using cached artwork bytes for notification");
                }
            }
            // Note: We intentionally don't use remote artworkUrl here because Windows Toast
            // can silently fail to display notifications when fetching remote images fails

            ConfigureAndShowToast(builder, TrackNotificationTag);
            _logger.LogDebug("Track notification shown: {Title} by {Artist}", track.Title, track.Artist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show track notification");
        }
    }

    /// <summary>
    /// Validates that an artwork URL is safe and accessible for toast notifications.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="uri">The parsed URI if valid.</param>
    /// <returns>True if the URL is valid for use in notifications.</returns>
    private static bool IsValidArtworkUrl(string url, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        // Only allow HTTP/HTTPS schemes for remote artwork
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// State change notifications are simpler than track notifications:
    /// <list type="bullet">
    ///   <item>Playing: Shows "Playback Started" with track info if available</item>
    ///   <item>Paused: Shows "Playback Paused"</item>
    ///   <item>Stopped/Idle: Shows "Playback Stopped"</item>
    /// </list>
    /// </para>
    /// <para>
    /// These notifications use the "state" tag and will replace previous state notifications.
    /// </para>
    /// </remarks>
    public void ShowPlaybackStateChanged(PlaybackState newState, TrackMetadata? track = null)
    {
        // Check the appropriate setting based on the state
        bool shouldShow = newState switch
        {
            PlaybackState.Playing => Settings.ShowOnPlaybackStart,
            PlaybackState.Paused => Settings.ShowOnPlaybackPause,
            PlaybackState.Stopped or PlaybackState.Idle => Settings.ShowOnPlaybackStop,
            _ => false
        };

        if (!ShouldShowNotification($"ShowOnPlayback{newState}", () => shouldShow))
            return;

        try
        {
            var (title, message) = newState switch
            {
                PlaybackState.Playing => ("Playback Started",
                    track != null ? $"{track.Title ?? "Unknown"} by {track.Artist ?? "Unknown"}" : "Music is now playing"),
                PlaybackState.Paused => ("Playback Paused",
                    track != null ? $"{track.Title ?? "Unknown"}" : "Music paused"),
                PlaybackState.Stopped => ("Playback Stopped", "Music has stopped"),
                PlaybackState.Idle => ("Playback Ended", "Queue finished"),
                PlaybackState.Error => ("Playback Error", "An error occurred during playback"),
                _ => ("Playback", newState.ToString())
            };

            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            ConfigureAndShowToast(builder, StateNotificationTag);
            _logger.LogDebug("State notification shown: {State}", newState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show playback state notification");
        }
    }

    /// <inheritdoc/>
    public void ShowConnected(string serverName)
    {
        if (!ShouldShowNotification(nameof(Settings.ShowOnConnect), () => Settings.ShowOnConnect))
            return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText("Connected")
                .AddText($"Connected to {serverName}");

            ConfigureAndShowToast(builder, ConnectionNotificationTag);
            _logger.LogDebug("Connection notification shown: {Server}", serverName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show connection notification");
        }
    }

    /// <inheritdoc/>
    public void ShowDisconnected(string serverName, string? reason = null)
    {
        if (!ShouldShowNotification(nameof(Settings.ShowOnDisconnect), () => Settings.ShowOnDisconnect))
            return;

        try
        {
            var message = string.IsNullOrEmpty(reason)
                ? $"Disconnected from {serverName}"
                : $"Disconnected from {serverName}: {reason}";

            var builder = new ToastContentBuilder()
                .AddText("Disconnected")
                .AddText(message);

            ConfigureAndShowToast(builder, ConnectionNotificationTag);
            _logger.LogDebug("Disconnection notification shown: {Server} - {Reason}", serverName, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show disconnection notification");
        }
    }

    /// <inheritdoc/>
    public void ShowInfo(string title, string message)
    {
        if (!ShouldShowNotification("Info", () => true))
            return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            ConfigureAndShowToast(builder, "info");
            _logger.LogDebug("Info notification shown: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show info notification");
        }
    }

    /// <inheritdoc/>
    public void ShowError(string title, string message)
    {
        // Errors are always shown regardless of OnlyWhenMinimized setting
        if (!Settings.Enabled)
        {
            _logger.LogDebug("Skipping error notification - notifications disabled");
            return;
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            ConfigureAndShowToast(builder, "error");
            _logger.LogWarning("Error notification shown: {Title} - {Message}", title, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show error notification");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This clears all Sendspin notifications from the Windows Action Center.
    /// It does not affect notifications from other applications.
    /// </remarks>
    public void ClearAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
            _logger.LogDebug("All notifications cleared");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear notifications");
        }
    }

    /// <summary>
    /// Checks whether a notification should be shown based on settings and window state.
    /// </summary>
    /// <param name="settingName">Name of the setting being checked (for logging).</param>
    /// <param name="settingCheck">Function that returns the setting value.</param>
    /// <returns>True if the notification should be shown.</returns>
    private bool ShouldShowNotification(string settingName, Func<bool> settingCheck)
    {
        // Master switch
        if (!Settings.Enabled)
        {
            _logger.LogTrace("Notification suppressed - notifications disabled globally");
            return false;
        }

        // Specific setting check
        if (!settingCheck())
        {
            _logger.LogTrace("Notification suppressed - {Setting} is disabled", settingName);
            return false;
        }

        // Window visibility check
        if (Settings.OnlyWhenMinimized && _isWindowVisibleCallback != null)
        {
            try
            {
                if (_isWindowVisibleCallback())
                {
                    _logger.LogTrace("Notification suppressed - window is visible");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Don't fail notification due to visibility check error
                _logger.LogWarning(ex, "Error checking window visibility, proceeding with notification");
            }
        }

        return true;
    }

    /// <summary>
    /// Configures common toast settings and displays the notification.
    /// </summary>
    /// <param name="builder">The toast builder to configure and show.</param>
    /// <param name="tag">Tag for the notification (allows replacement of same-tagged notifications).</param>
    private void ConfigureAndShowToast(ToastContentBuilder builder, string tag)
    {
        // Configure audio based on settings
        if (!Settings.PlaySound)
        {
            builder.AddAudio(new ToastAudio { Silent = true });
        }

        // Show the notification with tag for replacement behavior
        // Using the same tag causes new notifications to replace old ones
        builder.Show(toast =>
        {
            toast.Tag = tag;
            toast.Group = AppId;
        });
    }

    /// <summary>
    /// Builds the secondary line of text from artist and album.
    /// </summary>
    /// <param name="artist">The artist name (may be null).</param>
    /// <param name="album">The album name (may be null).</param>
    /// <returns>Formatted string like "Artist • Album", "Artist", "Album", or empty string.</returns>
    private static string BuildSecondaryLine(string? artist, string? album)
    {
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        var hasAlbum = !string.IsNullOrWhiteSpace(album);

        return (hasArtist, hasAlbum) switch
        {
            (true, true) => $"{artist} • {album}",
            (true, false) => artist!,
            (false, true) => album!,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Saves album artwork to a temporary file for use in toast notifications.
    /// </summary>
    /// <param name="imageData">The image data (PNG or JPEG).</param>
    /// <returns>The file URI path, or null if saving failed.</returns>
    /// <remarks>
    /// <para>
    /// Windows Toast notifications require images to be referenced by file URI.
    /// This method saves the artwork to a temp file with a unique name based on
    /// the image content hash, allowing reuse of the same file for repeated tracks.
    /// </para>
    /// <para>
    /// Old artwork files are cleaned up periodically to prevent temp directory bloat.
    /// </para>
    /// </remarks>
    private string? SaveArtworkToTemp(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0)
            return null;

        try
        {
            // Use a hash of the image data for the filename to enable caching
            // This avoids creating duplicate files for the same artwork
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageData))[..16];
            var fileName = $"artwork_{hash}.png";
            var filePath = Path.Combine(_tempArtworkDirectory, fileName);

            // Only write if file doesn't exist (caching)
            if (!File.Exists(filePath))
            {
                File.WriteAllBytes(filePath, imageData);
                _logger.LogTrace("Saved artwork to {Path}", filePath);

                // Clean up old artwork files (keep last 20)
                CleanupOldArtwork(maxFiles: 20);
            }

            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save artwork to temp file");
            return null;
        }
    }

    /// <summary>
    /// Removes old artwork files to prevent temp directory bloat.
    /// </summary>
    /// <param name="maxFiles">Maximum number of artwork files to keep.</param>
    private void CleanupOldArtwork(int maxFiles)
    {
        try
        {
            var files = Directory.GetFiles(_tempArtworkDirectory, "artwork_*.png")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastAccessTime)
                .Skip(maxFiles)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                    _logger.LogTrace("Deleted old artwork: {Path}", file.Name);
                }
                catch
                {
                    // Ignore deletion errors (file may be in use)
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error during artwork cleanup");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Clear notifications when service is disposed
        try
        {
            ClearAll();
        }
        catch
        {
            // Ignore errors during disposal
        }

        // Clean up temp artwork directory
        try
        {
            if (Directory.Exists(_tempArtworkDirectory))
            {
                Directory.Delete(_tempArtworkDirectory, recursive: true);
                _logger.LogDebug("Cleaned up artwork temp directory");
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to clean up artwork directory");
        }

        _logger.LogInformation("Windows Toast notification service disposed");
    }
}
