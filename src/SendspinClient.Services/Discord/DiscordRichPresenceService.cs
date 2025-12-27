using DiscordRPC;
using DiscordRPC.Logging;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;

namespace SendspinClient.Services.Discord;

/// <summary>
/// Discord Rich Presence service implementation.
/// </summary>
/// <remarks>
/// <para>
/// This service integrates with Discord to display the currently playing track
/// in the user's Discord profile activity status.
/// </para>
/// <para>
/// Key features:
/// - Automatic debouncing to respect Discord's rate limits (~15 updates/minute)
/// - Graceful handling when Discord is not running
/// - Proper cleanup to avoid ghost presence on exit
/// </para>
/// </remarks>
public sealed class DiscordRichPresenceService : IDiscordRichPresenceService
{
    private readonly ILogger<DiscordRichPresenceService> _logger;
    private readonly string _applicationId;

    private DiscordRpcClient? _client;
    private bool _isEnabled;
    private bool _isConnected;
    private bool _disposed;

    // Debouncing state
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private string? _lastTrackKey;
    private PlaybackState _lastState;
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromSeconds(4);

    // Discord asset keys (must match assets uploaded to Discord Developer Portal)
    private const string LargeImageKey = "sendspin_logo";
    private const string LargeImageText = "SendSpin";

    /// <summary>
    /// Initializes a new instance of the Discord Rich Presence service.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="applicationId">The Discord Application ID from the Developer Portal.</param>
    public DiscordRichPresenceService(
        ILogger<DiscordRichPresenceService> logger,
        string applicationId)
    {
        _logger = logger;
        _applicationId = applicationId;

        if (string.IsNullOrWhiteSpace(_applicationId))
        {
            _logger.LogWarning("Discord Application ID is not configured. Rich Presence will be disabled.");
        }
    }

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public void Enable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscordRichPresenceService));
        }

        if (_isEnabled)
        {
            _logger.LogDebug("Discord Rich Presence is already enabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(_applicationId))
        {
            _logger.LogWarning("Cannot enable Discord Rich Presence: Application ID is not configured");
            return;
        }

        _logger.LogInformation("Enabling Discord Rich Presence");

        try
        {
            _client = new DiscordRpcClient(_applicationId)
            {
                Logger = new DiscordLoggerAdapter(_logger)
            };

            _client.OnReady += (sender, e) =>
            {
                _isConnected = true;
                _logger.LogInformation("Connected to Discord as {Username}#{Discriminator}",
                    e.User.Username, e.User.Discriminator);
            };

            _client.OnConnectionFailed += (sender, e) =>
            {
                _isConnected = false;
                _logger.LogDebug("Discord connection failed - Discord may not be running");
            };

            _client.OnError += (sender, e) =>
            {
                _logger.LogWarning("Discord RPC error: {Message}", e.Message);
            };

            _client.OnClose += (sender, e) =>
            {
                _isConnected = false;
                _logger.LogDebug("Discord connection closed: {Reason}", e.Reason);
            };

            _client.Initialize();
            _isEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Discord Rich Presence");
            _client?.Dispose();
            _client = null;
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        if (!_isEnabled)
        {
            return;
        }

        _logger.LogInformation("Disabling Discord Rich Presence");

        ClearPresence();
        DisposeClient();

        _isEnabled = false;
        _isConnected = false;
    }

    /// <inheritdoc />
    public void UpdatePresence(TrackMetadata? track, PlaybackState state, double positionSeconds)
    {
        if (!_isEnabled || _client == null || _disposed)
        {
            return;
        }

        // Clear presence if not playing or no track
        if (track == null || state != PlaybackState.Playing)
        {
            // Only clear if we had something before
            if (_lastTrackKey != null || _lastState == PlaybackState.Playing)
            {
                ClearPresence();
            }
            return;
        }

        // Generate a unique key for this track
        var trackKey = $"{track.Title}|{track.Artist}|{track.Uri}";

        // Check if this is a duplicate update (same track, same state)
        var now = DateTime.UtcNow;
        var timeSinceLastUpdate = now - _lastUpdateTime;

        // Skip if same track and not enough time has passed (debounce)
        if (trackKey == _lastTrackKey && _lastState == state && timeSinceLastUpdate < MinUpdateInterval)
        {
            _logger.LogTrace("Skipping Discord update - debounced (same track, {Ms}ms since last)",
                timeSinceLastUpdate.TotalMilliseconds);
            return;
        }

        // Build the presence with "Listening" activity type for music
        var presence = new RichPresence
        {
            Type = ActivityType.Listening,
            Details = Truncate(track.Title ?? "Unknown Track", 128),
            State = Truncate(track.Artist ?? "Unknown Artist", 128),
            Assets = new Assets
            {
                LargeImageKey = LargeImageKey,
                LargeImageText = Truncate(track.Album ?? LargeImageText, 128)
            }
        };

        // Add elapsed time timestamp
        if (positionSeconds >= 0)
        {
            var startTime = DateTime.UtcNow - TimeSpan.FromSeconds(positionSeconds);
            presence.Timestamps = new Timestamps(startTime);
        }

        try
        {
            _client.SetPresence(presence);
            _lastUpdateTime = now;
            _lastTrackKey = trackKey;
            _lastState = state;

            _logger.LogDebug("Updated Discord presence: {Title} by {Artist}",
                track.Title, track.Artist);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Discord presence");
        }
    }

    /// <inheritdoc />
    public void ClearPresence()
    {
        if (_client == null || _disposed)
        {
            return;
        }

        try
        {
            _client.ClearPresence();
            _lastTrackKey = null;
            _lastState = PlaybackState.Idle;
            _logger.LogDebug("Cleared Discord presence");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear Discord presence");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disable();
    }

    private void DisposeClient()
    {
        if (_client != null)
        {
            try
            {
                _client.ClearPresence();
                _client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing Discord client");
            }
            finally
            {
                _client = null;
            }
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Adapter to bridge Discord RPC logging to Microsoft.Extensions.Logging.
    /// </summary>
    private sealed class DiscordLoggerAdapter : DiscordRPC.Logging.ILogger
    {
        private readonly ILogger<DiscordRichPresenceService> _logger;

        public DiscordLoggerAdapter(ILogger<DiscordRichPresenceService> logger)
        {
            _logger = logger;
        }

        public DiscordRPC.Logging.LogLevel Level { get; set; } = DiscordRPC.Logging.LogLevel.Info;

        public void Error(string message, params object[] args)
        {
            _logger.LogError(message, args);
        }

        public void Warning(string message, params object[] args)
        {
            _logger.LogWarning(message, args);
        }

        public void Info(string message, params object[] args)
        {
            _logger.LogInformation(message, args);
        }

        public void Trace(string message, params object[] args)
        {
            _logger.LogTrace(message, args);
        }
    }
}
