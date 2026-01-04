using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using SendspinClient.Configuration;
using SendspinClient.Models;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using SendspinClient.Services.Discord;
using SendspinClient.Services.Notifications;
using SendspinClient.Views;

namespace SendspinClient.ViewModels;

/// <summary>
/// Main view model supporting both connection modes:
/// - Client-initiated: We discover servers via mDNS and connect to them (like CLI)
/// - Server-initiated: We advertise via mDNS and servers connect to us (fallback)
/// - Manual: We connect to a server by URL (for cross-subnet scenarios)
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly SendspinHostService _hostService;
    private readonly MdnsServerDiscovery _serverDiscovery;
    private readonly IAudioPipeline _audioPipeline;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly INotificationService _notificationService;
    private readonly IDiscordRichPresenceService _discordService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClientCapabilities _clientCapabilities;
    private SendspinClientService? _manualClient;
    private ISendspinConnection? _manualConnection;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private string? _lastArtworkUrl;
    private string? _autoConnectedServerId;
    private bool _autoReconnectEnabled = true;
    private bool _isUpdatingFromServer;
    private CancellationTokenSource? _volumeDebouncesCts;

    /// <summary>
    /// Tracks the previous track identifier to detect actual track changes vs. metadata updates.
    /// Uses a combination of title and artist since URI is not always available.
    /// </summary>
    private string? _previousTrackId;

    /// <summary>
    /// Timer for smooth position interpolation between server updates.
    /// </summary>
    private readonly DispatcherTimer _positionTimer;

    /// <summary>
    /// The last position value received from the server.
    /// Used as anchor point for local interpolation.
    /// </summary>
    private double _lastServerPosition;

    /// <summary>
    /// Timestamp when we received the last server position update.
    /// Used to calculate elapsed time for interpolation.
    /// </summary>
    private DateTime _lastServerPositionUpdate = DateTime.MinValue;

    /// <summary>
    /// Gets the application version string for display in the UI.
    /// </summary>
    public string AppVersion => GetAppVersion();

    /// <summary>
    /// Gets or sets whether the host service is running and advertising via mDNS.
    /// </summary>
    [ObservableProperty]
    private bool _isHosting;

    /// <summary>
    /// Gets or sets the unique client identifier used for mDNS advertisement and server identification.
    /// </summary>
    [ObservableProperty]
    private string? _clientId;

    /// <summary>
    /// Gets or sets the display name of the currently connected Sendspin server.
    /// </summary>
    [ObservableProperty]
    private string? _connectedServerName;

    /// <summary>
    /// Gets or sets the metadata for the currently playing track (title, artist, album, etc.).
    /// </summary>
    [ObservableProperty]
    private TrackMetadata? _currentTrack;

    /// <summary>
    /// Gets or sets the current playback state (Idle, Playing, Paused, Buffering, etc.).
    /// </summary>
    [ObservableProperty]
    private PlaybackState _playbackState = PlaybackState.Idle;

    /// <summary>
    /// Gets or sets the current volume level (0-100).
    /// Changes are debounced and sent to the server.
    /// </summary>
    [ObservableProperty]
    private int _volume = 100;

    /// <summary>
    /// Gets or sets whether audio output is muted.
    /// </summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>
    /// Gets or sets the current playback position in seconds.
    /// </summary>
    [ObservableProperty]
    private double _position;

    /// <summary>
    /// Gets or sets the total duration of the current track in seconds.
    /// </summary>
    [ObservableProperty]
    private double _duration;

    /// <summary>
    /// Gets or sets the album artwork image data as a byte array.
    /// Null when no artwork is available.
    /// </summary>
    [ObservableProperty]
    private byte[]? _albumArtwork;

    /// <summary>
    /// Gets or sets the status message displayed in the UI.
    /// Shows connection state, errors, or "Now Playing" information.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Starting...";

    /// <summary>
    /// Gets or sets the WebSocket URL for manual server connection.
    /// Used for cross-subnet scenarios where mDNS discovery doesn't work.
    /// </summary>
    [ObservableProperty]
    private string _manualServerUrl = string.Empty;

    /// <summary>
    /// Gets or sets whether a connection attempt is currently in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>
    /// Gets or sets the current audio codec description (e.g., "OPUS 48kHz", "FLAC 44.1kHz 24-bit").
    /// Null when not playing audio.
    /// </summary>
    [ObservableProperty]
    private string? _currentCodec;

    /// <summary>
    /// Gets or sets whether the settings panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>
    /// Gets or sets the current log level setting.
    /// </summary>
    [ObservableProperty]
    private string _settingsLogLevel = "Information";

    /// <summary>
    /// Gets or sets whether file logging is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsEnableFileLogging = true;

    /// <summary>
    /// Gets or sets whether console logging is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsEnableConsoleLogging;

    /// <summary>
    /// Gets or sets the static delay in milliseconds for audio sync tuning.
    /// Positive values delay playback (play later), negative advance it (play earlier).
    /// </summary>
    [ObservableProperty]
    private double _settingsStaticDelayMs;

    /// <summary>
    /// Gets or sets whether notifications are enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsShowNotifications = true;

    /// <summary>
    /// Gets or sets whether Discord Rich Presence is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsShowDiscordPresence;

    /// <summary>
    /// Gets or sets the player name shown to servers.
    /// Defaults to the computer name.
    /// </summary>
    [ObservableProperty]
    private string _settingsPlayerName = Environment.MachineName;

    /// <summary>
    /// Gets or sets the selected audio output device.
    /// </summary>
    [ObservableProperty]
    private AudioDeviceInfo? _settingsSelectedAudioDevice;

    /// <summary>
    /// Gets the available audio output devices.
    /// </summary>
    public ObservableCollection<AudioDeviceInfo> AvailableAudioDevices { get; } = new();

    /// <summary>
    /// Gets the path where log files are stored.
    /// </summary>
    public string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sendspin",
        "logs");

    /// <summary>
    /// Gets the available log levels for the settings dropdown.
    /// </summary>
    public string[] AvailableLogLevels { get; } = new[]
    {
        "Verbose",
        "Debug",
        "Information",
        "Warning",
        "Error"
    };

    /// <summary>
    /// Gets the collection of currently connected Sendspin servers.
    /// Updated when servers connect or disconnect via the host service.
    /// </summary>
    public ObservableCollection<ConnectedServerInfo> ConnectedServers { get; } = new();

    /// <summary>
    /// Gets the collection of discovered Sendspin servers available for connection.
    /// Updated via mDNS discovery.
    /// </summary>
    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = new();

    /// <summary>
    /// Gets or sets whether the auto-connect confirmation dialog is visible.
    /// </summary>
    [ObservableProperty]
    private bool _showAutoConnectDialog;

    /// <summary>
    /// Gets or sets the server pending connection confirmation.
    /// Used with the auto-connect dialog.
    /// </summary>
    [ObservableProperty]
    private DiscoveredServer? _pendingConnectionServer;

    /// <summary>
    /// Gets or sets the server ID to auto-connect to when discovered.
    /// Empty string means no auto-connect preference saved.
    /// </summary>
    [ObservableProperty]
    private string _autoConnectServerId = string.Empty;

    /// <summary>
    /// Gets whether we're currently searching for servers (no servers found yet).
    /// </summary>
    public bool IsSearchingForServers => DiscoveredServers.Count == 0 && IsHosting;

    /// <summary>
    /// Gets whether the client is connected to any Sendspin server,
    /// either via manual connection or through the host service.
    /// </summary>
    public bool IsConnected => ConnectedServers.Count > 0 || _manualClient?.ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// Gets whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// Gets the current position formatted as a time string (e.g., "3:45" or "1:23:45").
    /// </summary>
    public string PositionFormatted => FormatTime(Position);

    /// <summary>
    /// Gets the total duration formatted as a time string (e.g., "3:45" or "1:23:45").
    /// </summary>
    public string DurationFormatted => FormatTime(Duration);

    /// <summary>
    /// Gets the progress percentage (0-100) for progress bar display.
    /// </summary>
    public double ProgressPercent => Duration > 0 ? (Position / Duration) * 100 : 0;

    /// <summary>
    /// Formats seconds as a time string (M:SS or H:MM:SS for long tracks).
    /// </summary>
    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "0:00";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    public MainViewModel(
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        SendspinHostService hostService,
        MdnsServerDiscovery serverDiscovery,
        IAudioPipeline audioPipeline,
        IClockSynchronizer clockSynchronizer,
        INotificationService notificationService,
        IDiscordRichPresenceService discordService,
        IHttpClientFactory httpClientFactory,
        ClientCapabilities clientCapabilities)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _hostService = hostService;
        _serverDiscovery = serverDiscovery;
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        _notificationService = notificationService;
        _discordService = discordService;
        _httpClientFactory = httpClientFactory;
        _clientCapabilities = clientCapabilities;

        // Load current logging settings
        LoadLoggingSettings();

        // Subscribe to host events (server-initiated mode fallback)
        _hostService.ServerConnected += OnServerConnected;
        _hostService.ServerDisconnected += OnServerDisconnected;
        _hostService.GroupStateChanged += OnGroupStateChanged;
        _hostService.ArtworkReceived += OnArtworkReceived;

        // Subscribe to server discovery events (client-initiated mode - primary)
        _serverDiscovery.ServerFound += OnDiscoveredServerFound;
        _serverDiscovery.ServerLost += OnDiscoveredServerLost;

        // Subscribe to audio pipeline events for codec display
        _audioPipeline.StateChanged += OnAudioPipelineStateChanged;

        // Initialize position interpolation timer (250ms interval for smooth progress)
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();
    }

    /// <summary>
    /// Timer tick handler for smooth position interpolation.
    /// Calculates current position based on last server update + elapsed time.
    /// </summary>
    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        // Only interpolate when playing and we have valid anchor data
        if (PlaybackState != PlaybackState.Playing ||
            _lastServerPositionUpdate == DateTime.MinValue ||
            Duration <= 0)
        {
            return;
        }

        // Calculate interpolated position
        var elapsed = (DateTime.UtcNow - _lastServerPositionUpdate).TotalSeconds;
        var interpolatedPosition = _lastServerPosition + elapsed;

        // Clamp to valid range (don't exceed duration)
        interpolatedPosition = Math.Min(interpolatedPosition, Duration);

        // Update position (this triggers OnPositionChanged which updates UI bindings)
        Position = interpolatedPosition;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing MainViewModel");

        try
        {
            // Start host service (server-initiated mode - fallback)
            StatusMessage = "Starting host service...";
            await _hostService.StartAsync();
            ClientId = _hostService.ClientId;
            IsHosting = true;

            _logger.LogInformation("Host service started, advertising as {ClientId}", ClientId);

            // Start server discovery (client-initiated mode - primary)
            StatusMessage = "Discovering Sendspin servers...";
            await _serverDiscovery.StartAsync();

            _logger.LogInformation("Server discovery started, looking for _sendspin-server._tcp");

            StatusMessage = $"Searching for servers...\nClient ID: {ClientId}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize");
            StatusMessage = $"Failed to start: {ex.Message}";
            SetError($"Failed to initialize: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (!IsConnected) return;

        try
        {
            var command = PlaybackState == PlaybackState.Playing
                ? Commands.Pause
                : Commands.Play;
            await SendCommandToActiveClientAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send play/pause command");
        }
    }

    [RelayCommand]
    private async Task NextTrackAsync()
    {
        if (!IsConnected) return;

        try
        {
            await SendCommandToActiveClientAsync(Commands.Next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send next command");
        }
    }

    [RelayCommand]
    private async Task PreviousTrackAsync()
    {
        if (!IsConnected) return;

        try
        {
            await SendCommandToActiveClientAsync(Commands.Previous);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send previous command");
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        if (!IsConnected) return;

        try
        {
            // Toggle the local mute state and send it to the server
            var newMutedState = !IsMuted;
            _logger.LogDebug("Toggling mute: {Muted}", newMutedState);

            // Send player state to notify Music Assistant of our mute change
            // This is what JS/Python clients do - they report state rather than sending commands
            await SendPlayerStateToActiveClientAsync(Volume, newMutedState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle mute");
        }
    }

    /// <summary>
    /// Sends a command to the active client (manual client or host service).
    /// </summary>
    private async Task SendCommandToActiveClientAsync(string command, Dictionary<string, object>? parameters = null)
    {
        // Prefer manual client (discovery/manual connection mode)
        if (_manualClient?.ConnectionState == ConnectionState.Connected)
        {
            _logger.LogDebug("Sending command {Command} via manual client", command);
            await _manualClient.SendCommandAsync(command, parameters);
        }
        // Fall back to host service (server-initiated connection mode)
        else if (ConnectedServers.Count > 0)
        {
            _logger.LogDebug("Sending command {Command} via host service", command);
            await _hostService.SendCommandAsync(command, parameters);
        }
        else
        {
            _logger.LogWarning("No active connection to send command {Command}", command);
        }
    }

    /// <summary>
    /// Sends the current player state (volume, muted) to the server.
    /// This notifies Music Assistant of our current volume/mute state.
    /// </summary>
    private async Task SendPlayerStateToActiveClientAsync(int volume, bool muted)
    {
        // Prefer manual client (discovery/manual connection mode)
        if (_manualClient?.ConnectionState == ConnectionState.Connected)
        {
            _logger.LogDebug("Sending player state via manual client: Volume={Volume}, Muted={Muted}", volume, muted);
            await _manualClient.SendPlayerStateAsync(volume, muted);
        }
        // Fall back to host service (server-initiated connection mode)
        else if (ConnectedServers.Count > 0)
        {
            _logger.LogDebug("Sending player state via host service: Volume={Volume}, Muted={Muted}", volume, muted);
            await _hostService.SendPlayerStateAsync(volume, muted);
        }
        else
        {
            _logger.LogWarning("No active connection to send player state");
        }
    }

    /// <summary>
    /// Manually connect to a Sendspin server by URL (for cross-subnet scenarios).
    /// </summary>
    [RelayCommand]
    private async Task ConnectToServerAsync()
    {
        // Clear any previous error when starting a new connection attempt
        ClearError();

        if (string.IsNullOrWhiteSpace(ManualServerUrl))
        {
            StatusMessage = "Please enter a server URL";
            return;
        }

        if (_manualClient?.ConnectionState == ConnectionState.Connected)
        {
            _logger.LogWarning("Already connected to a server");
            return;
        }

        IsConnecting = true;
        StatusMessage = $"Connecting to {ManualServerUrl}...";

        try
        {
            // Normalize the URL - auto-prepend ws:// if no scheme provided
            var urlToParse = ManualServerUrl.Trim();
            if (!urlToParse.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !urlToParse.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                urlToParse = "ws://" + urlToParse;
            }

            // Parse the URL
            if (!Uri.TryCreate(urlToParse, UriKind.Absolute, out var serverUri))
            {
                StatusMessage = "Invalid URL format. Example: 10.0.2.8:8927";
                IsConnecting = false;
                return;
            }

            // Ensure the path includes /sendspin if not specified
            if (string.IsNullOrEmpty(serverUri.AbsolutePath) || serverUri.AbsolutePath == "/")
            {
                serverUri = new Uri(serverUri, "/sendspin");
            }

            _logger.LogInformation("Normalized URL: {OriginalUrl} -> {NormalizedUrl}", ManualServerUrl, serverUri);

            // Create the connection and client service
            _manualConnection = new SendspinConnection(
                _loggerFactory.CreateLogger<SendspinConnection>());

            _manualClient = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                _manualConnection,
                clockSynchronizer: _clockSynchronizer,
                capabilities: _clientCapabilities,
                audioPipeline: _audioPipeline);

            // Subscribe to events
            _manualClient.ConnectionStateChanged += OnManualClientConnectionStateChanged;
            _manualClient.GroupStateChanged += OnManualClientGroupStateChanged;
            _manualClient.ArtworkReceived += OnManualClientArtworkReceived;

            // Connect
            await _manualClient.ConnectAsync(serverUri);

            _logger.LogInformation("Manual connection initiated to {ServerUri}", serverUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to {ServerUrl}", ManualServerUrl);
            StatusMessage = $"Connection failed: {ex.Message}";
            SetError($"Failed to connect: {ex.Message}");

            // Cleanup on failure
            await CleanupManualClientAsync();
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// Disconnect from the manually connected server.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectFromServerAsync()
    {
        if (_manualClient == null)
            return;

        _logger.LogInformation("Disconnecting from manual server");
        StatusMessage = "Disconnecting...";

        await CleanupManualClientAsync();

        ConnectedServerName = null;
        CurrentTrack = null;
        PlaybackState = PlaybackState.Idle;
        AlbumArtwork = null;
        _lastArtworkUrl = null;
        StatusMessage = $"Disconnected. Waiting for connections...\nClient ID: {ClientId}";
        OnPropertyChanged(nameof(IsConnected));
    }

    private async Task CleanupManualClientAsync()
    {
        // Use semaphore to prevent concurrent cleanup calls from racing
        // This can happen when connection failure triggers both an event handler
        // and an exception handler that both try to cleanup
        await _cleanupLock.WaitAsync();
        try
        {
            if (_manualClient == null)
                return;

            _manualClient.ConnectionStateChanged -= OnManualClientConnectionStateChanged;
            _manualClient.GroupStateChanged -= OnManualClientGroupStateChanged;
            _manualClient.ArtworkReceived -= OnManualClientArtworkReceived;

            try
            {
                await _manualClient.DisconnectAsync();
                await _manualClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during manual client cleanup");
            }

            _manualClient = null;
            _manualConnection = null;
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private void OnManualClientConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _logger.LogInformation("Client state: {OldState} -> {NewState}",
                e.OldState, e.NewState);

            if (e.NewState == ConnectionState.Connected)
            {
                ConnectedServerName = _manualClient?.ServerName ?? "Unknown Server";
                StatusMessage = $"Connected to {ConnectedServerName}";
                OnPropertyChanged(nameof(IsConnected));

                // Show toast notification for connection
                _notificationService.ShowConnected(ConnectedServerName);

                // Stop advertising so other servers don't try to connect to us
                _hostService.StopAdvertisingAsync().SafeFireAndForget(_logger);
            }
            else if (e.NewState == ConnectionState.Disconnected)
            {
                var wasAutoConnected = _autoConnectedServerId != null;
                ConnectedServerName = null;
                CurrentTrack = null;
                PlaybackState = PlaybackState.Idle;
                AlbumArtwork = null;
                _lastArtworkUrl = null;
                _autoConnectedServerId = null;

                if (wasAutoConnected && _autoReconnectEnabled)
                {
                    StatusMessage = $"Disconnected. Searching for servers...";
                    // Discovery is still running, it will auto-reconnect when server reappears
                    _logger.LogInformation("Will auto-reconnect when server is rediscovered");
                }
                else
                {
                    StatusMessage = $"Disconnected: {e.Reason ?? "Unknown"}";
                }

                OnPropertyChanged(nameof(IsConnected));

                // Resume advertising so servers can discover us again
                _hostService.StartAdvertisingAsync().SafeFireAndForget(_logger);

                // Cleanup in background to avoid blocking UI
                CleanupManualClientAsync().SafeFireAndForget(_logger);
            }
        });
    }

    private void OnManualClientGroupStateChanged(object? sender, GroupState group)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _isUpdatingFromServer = true;
            try
            {
                PlaybackState = group.PlaybackState;
                Volume = group.Volume;
                IsMuted = group.Muted;
                CurrentTrack = group.Metadata;

                if (group.Metadata?.Duration.HasValue == true)
                {
                    Duration = group.Metadata.Duration.Value;
                }

                // Update position from server and set anchor for interpolation
                if (group.Metadata?.Position.HasValue == true)
                {
                    _lastServerPosition = group.Metadata.Position.Value;
                    _lastServerPositionUpdate = DateTime.UtcNow;
                    Position = _lastServerPosition;
                }

                if (CurrentTrack != null)
                {
                    StatusMessage = $"Now Playing: {CurrentTrack.Title ?? "Unknown"}\n" +
                                   $"by {CurrentTrack.Artist ?? "Unknown Artist"}";
                }

                // Fetch artwork if URL changed
                var artworkUrl = group.Metadata?.ArtworkUrl;
                if (!string.IsNullOrEmpty(artworkUrl) && artworkUrl != _lastArtworkUrl)
                {
                    _lastArtworkUrl = artworkUrl;
                    FetchArtworkAsync(artworkUrl).SafeFireAndForget(_logger);
                }

                _logger.LogDebug("Manual client group state updated: {State}", group.PlaybackState);
            }
            finally
            {
                _isUpdatingFromServer = false;
            }
        });
    }

    private async Task FetchArtworkAsync(string url)
    {
        // Validate URL before fetching to prevent SSRF attacks
        if (!IsValidArtworkUrl(url, out var artworkUri))
        {
            _logger.LogWarning("Blocked artwork fetch from invalid or unsafe URL: {Url}", url);
            return;
        }

        try
        {
            _logger.LogDebug("Fetching artwork from {Url}", url);
            using var httpClient = _httpClientFactory.CreateClient("Artwork");
            var imageData = await httpClient.GetByteArrayAsync(artworkUri);

            App.Current.Dispatcher.Invoke(() =>
            {
                AlbumArtwork = imageData;
                _logger.LogDebug("Artwork loaded: {Length} bytes from {Url}", imageData.Length, url);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch artwork from {Url}", url);
        }
    }

    /// <summary>
    /// Validates that an artwork URL is safe to fetch.
    /// Blocks non-HTTP schemes and localhost to prevent SSRF attacks.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="uri">The parsed URI if valid.</param>
    /// <returns>True if the URL is safe to fetch.</returns>
    private static bool IsValidArtworkUrl(string url, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        // Only allow HTTP/HTTPS schemes
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        // Block localhost and loopback addresses to prevent accessing local services
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]")
            return false;

        // Check for loopback IP addresses
        if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))
            return false;

        return true;
    }

    private void OnManualClientArtworkReceived(object? sender, byte[] imageData)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            AlbumArtwork = imageData;
            _logger.LogDebug("Manual client artwork received: {Length} bytes", imageData.Length);
        });
    }

    private void OnServerConnected(object? sender, ConnectedServerInfo server)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            ConnectedServers.Add(server);
            ConnectedServerName = server.ServerName;
            StatusMessage = $"Connected to {server.ServerName}";
            OnPropertyChanged(nameof(IsConnected));

            // Show toast notification for connection
            _notificationService.ShowConnected(server.ServerName);

            _logger.LogInformation("Server connected: {ServerName} ({ServerId})",
                server.ServerName, server.ServerId);
        });
    }

    private void OnServerDisconnected(object? sender, string serverId)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var server = ConnectedServers.FirstOrDefault(s => s.ServerId == serverId);
            string? disconnectedServerName = server?.ServerName;

            if (server != null)
            {
                ConnectedServers.Remove(server);
                _logger.LogInformation("Server disconnected: {ServerId}", serverId);
            }

            if (ConnectedServers.Count == 0)
            {
                ConnectedServerName = null;
                CurrentTrack = null;
                PlaybackState = PlaybackState.Idle;
                StatusMessage = $"Waiting for connections...\nClient ID: {ClientId}";

                // Show toast notification for disconnection
                if (!string.IsNullOrEmpty(disconnectedServerName))
                {
                    _notificationService.ShowDisconnected(disconnectedServerName);
                }
            }
            else
            {
                ConnectedServerName = ConnectedServers[0].ServerName;
            }

            OnPropertyChanged(nameof(IsConnected));
        });
    }

    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _isUpdatingFromServer = true;
            try
            {
                PlaybackState = group.PlaybackState;
                Volume = group.Volume;
                IsMuted = group.Muted;
                CurrentTrack = group.Metadata;

                if (group.Metadata?.Duration.HasValue == true)
                {
                    Duration = group.Metadata.Duration.Value;
                }

                // Update position from server and set anchor for interpolation
                if (group.Metadata?.Position.HasValue == true)
                {
                    _lastServerPosition = group.Metadata.Position.Value;
                    _lastServerPositionUpdate = DateTime.UtcNow;
                    Position = _lastServerPosition;
                }

                // Update status with now playing info
                if (CurrentTrack != null)
                {
                    StatusMessage = $"Now Playing: {CurrentTrack.Title ?? "Unknown"}\n" +
                                   $"by {CurrentTrack.Artist ?? "Unknown Artist"}";
                }

                // Fetch artwork if URL changed
                var artworkUrl = group.Metadata?.ArtworkUrl;
                if (!string.IsNullOrEmpty(artworkUrl) && artworkUrl != _lastArtworkUrl)
                {
                    _lastArtworkUrl = artworkUrl;
                    FetchArtworkAsync(artworkUrl).SafeFireAndForget(_logger);
                }

                _logger.LogDebug("Group state updated: {State}, Track: {Track}",
                    group.PlaybackState, group.Metadata?.ToString());
            }
            finally
            {
                _isUpdatingFromServer = false;
            }
        });
    }

    private void OnArtworkReceived(object? sender, byte[] imageData)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            AlbumArtwork = imageData;
            _logger.LogDebug("Artwork received: {Length} bytes", imageData.Length);
        });
    }

    private void OnAudioPipelineStateChanged(object? sender, AudioPipelineState state)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var format = _audioPipeline.CurrentFormat;
            if (state == AudioPipelineState.Idle || format == null)
            {
                CurrentCodec = null;
            }
            else
            {
                // Format codec nicely: "OPUS 48kHz" or "FLAC 44.1kHz 24-bit"
                var codecName = format.Codec?.ToUpperInvariant() ?? "Unknown";
                var sampleRate = format.SampleRate >= 1000
                    ? $"{format.SampleRate / 1000.0:0.#}kHz"
                    : $"{format.SampleRate}Hz";

                CurrentCodec = format.BitDepth.HasValue && format.Codec?.ToLowerInvariant() != "opus"
                    ? $"{codecName} {sampleRate} {format.BitDepth}-bit"
                    : $"{codecName} {sampleRate}";
            }
        });
    }

    #region Server Discovery (Client-Initiated Mode)

    private void OnDiscoveredServerFound(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Discovered server: {Name} at {Host}:{Port}",
            server.Name, server.IpAddresses.FirstOrDefault(), server.Port);

        App.Current.Dispatcher.Invoke(() =>
        {
            // Add to discovered servers list for UI display
            if (!DiscoveredServers.Any(s => s.ServerId == server.ServerId))
            {
                DiscoveredServers.Add(server);
                OnPropertyChanged(nameof(IsSearchingForServers));
            }

            // Auto-connect only if we have a saved preference matching this server
            if (!IsConnected && _autoReconnectEnabled && !string.IsNullOrEmpty(AutoConnectServerId))
            {
                if (server.ServerId == AutoConnectServerId)
                {
                    _logger.LogInformation("Auto-connecting to saved server: {Name}", server.Name);
                    AutoConnectToServerAsync(server).SafeFireAndForget(_logger);
                }
            }
        });
    }

    private void OnDiscoveredServerLost(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Server lost: {Name} ({ServerId})", server.Name, server.ServerId);

        App.Current.Dispatcher.Invoke(() =>
        {
            // Remove from discovered servers list
            var existing = DiscoveredServers.FirstOrDefault(s => s.ServerId == server.ServerId);
            if (existing != null)
            {
                DiscoveredServers.Remove(existing);
                OnPropertyChanged(nameof(IsSearchingForServers));
            }
        });

        // If we were connected to this server, mark for reconnection
        if (_autoConnectedServerId == server.ServerId)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                StatusMessage = $"Server {server.Name} went offline. Searching for servers...";
            });
        }
    }

    private async Task AutoConnectToServerAsync(DiscoveredServer server)
    {
        // Clear any previous error when starting a new connection attempt
        ClearError();

        if (IsConnected)
        {
            _logger.LogDebug("Already connected, skipping auto-connect");
            return;
        }

        var ip = server.IpAddresses.FirstOrDefault();
        if (string.IsNullOrEmpty(ip))
        {
            _logger.LogWarning("No IP address for server {Name}", server.Name);
            return;
        }

        // Build WebSocket URL - check for path property
        var path = server.Properties.TryGetValue("path", out var p) ? p : "/sendspin";
        if (!path.StartsWith("/")) path = "/" + path;

        var url = $"ws://{ip}:{server.Port}{path}";
        _logger.LogInformation("Auto-connecting to discovered server: {Url}", url);

        IsConnecting = true;
        StatusMessage = $"Connecting to {server.Name}...";

        try
        {
            // Create the connection and client service
            _manualConnection = new SendspinConnection(
                _loggerFactory.CreateLogger<SendspinConnection>());

            _manualClient = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                _manualConnection,
                clockSynchronizer: _clockSynchronizer,
                capabilities: _clientCapabilities,
                audioPipeline: _audioPipeline);

            // Subscribe to events
            _manualClient.ConnectionStateChanged += OnManualClientConnectionStateChanged;
            _manualClient.GroupStateChanged += OnManualClientGroupStateChanged;
            _manualClient.ArtworkReceived += OnManualClientArtworkReceived;

            // Connect
            await _manualClient.ConnectAsync(new Uri(url));

            _autoConnectedServerId = server.ServerId;
            _logger.LogInformation("Auto-connected to {Name}", server.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-connect to {Name}", server.Name);
            StatusMessage = $"Failed to connect to {server.Name}. Searching for servers...";
            await CleanupManualClientAsync();
        }
        finally
        {
            IsConnecting = false;
        }
    }

    #endregion

    /// <summary>
    /// Called when the playback state changes.
    /// Updates UI bindings and optionally shows a toast notification.
    /// </summary>
    /// <param name="value">The new playback state.</param>
    partial void OnPlaybackStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        UpdateTrayToolTip();

        // Show playback state notification (if enabled in settings)
        // Note: Track change notifications are handled separately in OnCurrentTrackChanged
        // to avoid duplicate notifications when both state and track change together
        _notificationService.ShowPlaybackStateChanged(value, CurrentTrack);

        // Update Discord Rich Presence
        if (value == PlaybackState.Playing)
        {
            _discordService.UpdatePresence(CurrentTrack, value, Position);
        }
        else
        {
            _discordService.ClearPresence();
        }

        // Reset position interpolation anchor when playback stops
        if (value != PlaybackState.Playing)
        {
            _lastServerPositionUpdate = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Called when the playback position changes.
    /// Updates computed properties for progress display.
    /// </summary>
    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionFormatted));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    /// <summary>
    /// Called when the track duration changes.
    /// Updates computed properties for progress display.
    /// </summary>
    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationFormatted));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    /// <summary>
    /// Called when the current track changes.
    /// Detects actual track changes (vs. metadata updates) and shows notifications.
    /// </summary>
    /// <param name="value">The new track metadata.</param>
    partial void OnCurrentTrackChanged(TrackMetadata? value)
    {
        UpdateTrayToolTip();

        // Only show notification if this is actually a different track
        // Use URI if available, otherwise fall back to title+artist combination
        var currentTrackId = value?.Uri
            ?? (value != null ? $"{value.Title}|{value.Artist}" : null);

        if (value != null && !string.IsNullOrEmpty(currentTrackId) && currentTrackId != _previousTrackId)
        {
            _previousTrackId = currentTrackId;

            // Clear old artwork immediately - new artwork will be fetched async if available
            AlbumArtwork = null;
            _lastArtworkUrl = null;

            _logger.LogDebug("Track changed, showing notification: {TrackId}", currentTrackId);

            // Show track change notification (without artwork - it's disabled for reliability)
            _notificationService.ShowTrackChanged(value, null, null);

            // Update Discord Rich Presence with new track
            if (PlaybackState == PlaybackState.Playing)
            {
                _discordService.UpdatePresence(value, PlaybackState, Position);
            }
        }
        else if (value == null)
        {
            // Track cleared - reset artwork and progress
            AlbumArtwork = null;
            _lastArtworkUrl = null;
            _previousTrackId = null;
            Position = 0;
            Duration = 0;
            _lastServerPositionUpdate = DateTime.MinValue;

            // Clear Discord presence when track is cleared
            _discordService.ClearPresence();
        }
    }

    partial void OnConnectedServerNameChanged(string? value)
    {
        UpdateTrayToolTip();
    }

    private CancellationTokenSource? _staticDelayClearCts;
    private CancellationTokenSource? _staticDelaySaveCts;
    private CancellationTokenSource? _playerNameSaveCts;

    /// <summary>
    /// Called when the static delay setting changes.
    /// Applies the new delay immediately to the clock synchronizer.
    /// Buffer clear and save are debounced to avoid issues while dragging slider.
    /// </summary>
    partial void OnSettingsStaticDelayMsChanged(double value)
    {
        // Apply delay value immediately (this is cheap)
        _clockSynchronizer.StaticDelayMs = value;

        // Debounce buffer clear - only clear after user stops adjusting for 300ms
        _staticDelayClearCts?.Cancel();
        _staticDelayClearCts?.Dispose();
        _staticDelayClearCts = new CancellationTokenSource();
        var clearCts = _staticDelayClearCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, clearCts.Token);
                if (!clearCts.Token.IsCancellationRequested)
                {
                    _audioPipeline.Clear();
                    _logger.LogDebug("Static delay changed to {DelayMs}ms, audio buffer cleared", value);
                }
            }
            catch (OperationCanceledException)
            {
                // Slider still being adjusted, ignore
            }
        });

        // Debounce auto-save - save after user stops adjusting for 1 second
        _staticDelaySaveCts?.Cancel();
        _staticDelaySaveCts?.Dispose();
        _staticDelaySaveCts = new CancellationTokenSource();
        var saveCts = _staticDelaySaveCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, saveCts.Token);
                if (!saveCts.Token.IsCancellationRequested)
                {
                    await SaveStaticDelayAsync(value);
                }
            }
            catch (OperationCanceledException)
            {
                // Slider still being adjusted, ignore
            }
        });
    }

    /// <summary>
    /// Saves only the static delay setting to user appsettings.json.
    /// </summary>
    private async Task SaveStaticDelayAsync(double value)
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var audioSection = root["Audio"]?.AsObject() ?? new JsonObject();
            audioSection["StaticDelayMs"] = value;
            root["Audio"] = audioSection;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            _logger.LogInformation("Static delay saved: {DelayMs}ms", value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-save static delay");
        }
    }

    /// <summary>
    /// Called when the player name setting changes.
    /// Debounces and auto-saves after 1 second of no changes.
    /// </summary>
    partial void OnSettingsPlayerNameChanged(string value)
    {
        // Debounce auto-save - save after user stops typing for 1 second
        _playerNameSaveCts?.Cancel();
        _playerNameSaveCts?.Dispose();
        _playerNameSaveCts = new CancellationTokenSource();
        var saveCts = _playerNameSaveCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, saveCts.Token);
                if (!saveCts.Token.IsCancellationRequested)
                {
                    await SavePlayerNameAsync(value);
                }
            }
            catch (OperationCanceledException)
            {
                // User still typing, ignore
            }
        });
    }

    /// <summary>
    /// Saves only the player name setting to user appsettings.json.
    /// </summary>
    private async Task SavePlayerNameAsync(string name)
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var playerSection = root["Player"]?.AsObject() ?? new JsonObject();
            playerSection["Name"] = name;
            root["Player"] = playerSection;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            // Update client capabilities immediately
            _clientCapabilities.ClientName = name;
            _logger.LogInformation("Player name saved: {PlayerName}", name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-save player name");
        }
    }

    /// <summary>
    /// Called when the selected audio device changes.
    /// Immediately switches to the new device without requiring restart.
    /// </summary>
    partial void OnSettingsSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        if (value == null)
        {
            return;
        }

        // Get the device ID (null for system default)
        var deviceId = value.IsDefault ? null : value.DeviceId;

        _logger.LogInformation(
            "Audio device selection changed to: {DeviceName}",
            value.DisplayName);

        // Switch audio device asynchronously
        _ = SwitchAudioDeviceAsync(deviceId, value.DisplayName);
    }

    /// <summary>
    /// Switches to the specified audio device and saves the preference.
    /// </summary>
    private async Task SwitchAudioDeviceAsync(string? deviceId, string displayName)
    {
        try
        {
            // Only switch if the pipeline is running
            if (_audioPipeline.State != AudioPipelineState.Idle)
            {
                await _audioPipeline.SwitchDeviceAsync(deviceId);
                _logger.LogInformation("Audio output switched to: {DeviceName}", displayName);
            }

            // Save the device preference
            await SaveAudioDeviceAsync(deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch audio device to {DeviceName}", displayName);
        }
    }

    /// <summary>
    /// Saves the selected audio device to user settings.
    /// </summary>
    private async Task SaveAudioDeviceAsync(string? deviceId)
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var audioSection = root["Audio"]?.AsObject() ?? new JsonObject();
            audioSection["DeviceId"] = deviceId;
            root["Audio"] = audioSection;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            _logger.LogDebug("Audio device preference saved: {DeviceId}", deviceId ?? "default");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save audio device preference");
        }
    }

    /// <summary>
    /// Called when Discord Rich Presence setting changes.
    /// Enables or disables the Discord integration.
    /// </summary>
    partial void OnSettingsShowDiscordPresenceChanged(bool value)
    {
        if (value)
        {
            _discordService.Enable();
            // Update presence with current state if playing
            if (CurrentTrack != null && PlaybackState == PlaybackState.Playing)
            {
                _discordService.UpdatePresence(CurrentTrack, PlaybackState, Position);
            }
        }
        else
        {
            _discordService.Disable();
        }
    }

    partial void OnVolumeChanged(int value)
    {
        // Don't send commands when updating from server state
        if (_isUpdatingFromServer || !IsConnected)
            return;

        // Debounce volume changes to avoid spamming the server
        // Cancel any pending volume change and schedule a new one
        _volumeDebouncesCts?.Cancel();
        _volumeDebouncesCts?.Dispose();
        _volumeDebouncesCts = new CancellationTokenSource();
        _ = SendVolumeChangeDebounced(value, _volumeDebouncesCts.Token);
    }

    private async Task SendVolumeChangeDebounced(int volume, CancellationToken cancellationToken)
    {
        try
        {
            // Wait briefly before sending to allow slider to settle
            await Task.Delay(150, cancellationToken);

            _logger.LogDebug("Sending volume change: {Volume}", volume);

            // Send player state to notify Music Assistant of our volume change
            // This is what JS/Python clients do - they report state rather than sending commands
            await SendPlayerStateToActiveClientAsync(volume, IsMuted);
        }
        catch (OperationCanceledException)
        {
            // Debounce cancelled, a newer value will be sent
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send volume change");
        }
    }

    #region Settings

    /// <summary>
    /// Loads the current logging and audio settings from configuration.
    /// </summary>
    private void LoadLoggingSettings()
    {
        var settings = new LoggingSettings();
        _configuration.GetSection(LoggingSettings.SectionName).Bind(settings);

        SettingsLogLevel = settings.LogLevel ?? "Information";
        SettingsEnableFileLogging = settings.EnableFileLogging;
        SettingsEnableConsoleLogging = settings.EnableConsoleLogging;

        // Load audio settings
        SettingsStaticDelayMs = _configuration.GetValue<double>("Audio:StaticDelayMs", 0);

        // Load notification settings
        SettingsShowNotifications = _configuration.GetValue<bool>("Notifications:Enabled", true);
        _notificationService.IsEnabled = SettingsShowNotifications;

        // Load Discord Rich Presence settings
        SettingsShowDiscordPresence = _configuration.GetValue<bool>("Discord:Enabled", false);
        if (SettingsShowDiscordPresence)
        {
            _discordService.Enable();
        }

        // Load player name (default to computer name)
        SettingsPlayerName = _configuration.GetValue<string>("Player:Name", Environment.MachineName) ?? Environment.MachineName;

        // Load auto-connect server preference
        AutoConnectServerId = _configuration.GetValue<string>("Connection:AutoConnectServerId", string.Empty) ?? string.Empty;

        // Enumerate audio devices
        EnumerateAudioDevices();

        // Load saved audio device selection
        var savedDeviceId = _configuration.GetValue<string?>("Audio:DeviceId");
        SettingsSelectedAudioDevice = AvailableAudioDevices.FirstOrDefault(d => d.DeviceId == savedDeviceId)
            ?? AvailableAudioDevices.FirstOrDefault(d => d.IsDefault)
            ?? AudioDeviceInfo.Default;
    }

    /// <summary>
    /// Enumerates available audio output devices using WASAPI.
    /// </summary>
    private void EnumerateAudioDevices()
    {
        AvailableAudioDevices.Clear();

        // Add system default option first
        AvailableAudioDevices.Add(AudioDeviceInfo.Default);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                AvailableAudioDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = device.ID,
                    DisplayName = device.FriendlyName
                });
            }

            _logger.LogDebug("Found {Count} audio output devices", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate audio devices");
        }
    }

    /// <summary>
    /// Refreshes the list of available audio devices.
    /// </summary>
    [RelayCommand]
    private void RefreshAudioDevices()
    {
        var previousSelection = SettingsSelectedAudioDevice;
        EnumerateAudioDevices();

        // Try to preserve selection
        if (previousSelection != null)
        {
            SettingsSelectedAudioDevice = AvailableAudioDevices.FirstOrDefault(d => d.DeviceId == previousSelection.DeviceId)
                ?? AvailableAudioDevices.FirstOrDefault(d => d.IsDefault);
        }
    }

    /// <summary>
    /// Increments the static delay by 10ms.
    /// </summary>
    [RelayCommand]
    private void IncrementStaticDelay()
    {
        SettingsStaticDelayMs = Math.Min(5000, SettingsStaticDelayMs + 10);
    }

    /// <summary>
    /// Decrements the static delay by 10ms.
    /// </summary>
    [RelayCommand]
    private void DecrementStaticDelay()
    {
        SettingsStaticDelayMs = Math.Max(-5000, SettingsStaticDelayMs - 10);
    }

    /// <summary>
    /// Toggles the settings panel visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    /// <summary>
    /// Closes the settings panel.
    /// </summary>
    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    /// <summary>
    /// Initiates connection to a discovered server.
    /// Shows the auto-connect dialog to ask if this should be the default server.
    /// </summary>
    [RelayCommand]
    private void SelectServer(DiscoveredServer server)
    {
        PendingConnectionServer = server;
        ShowAutoConnectDialog = true;
    }

    /// <summary>
    /// Connects to the pending server without saving auto-connect preference.
    /// </summary>
    [RelayCommand]
    private async Task ConnectOnceAsync()
    {
        if (PendingConnectionServer == null) return;

        ShowAutoConnectDialog = false;
        var server = PendingConnectionServer;
        PendingConnectionServer = null;

        await ConnectToDiscoveredServerAsync(server);
    }

    /// <summary>
    /// Connects to the pending server and saves it as the auto-connect preference.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAlwaysAsync()
    {
        if (PendingConnectionServer == null) return;

        ShowAutoConnectDialog = false;
        var server = PendingConnectionServer;
        PendingConnectionServer = null;

        // Save the auto-connect preference
        AutoConnectServerId = server.ServerId;
        await SaveAutoConnectPreferenceAsync(server.ServerId);

        await ConnectToDiscoveredServerAsync(server);
    }

    /// <summary>
    /// Cancels the pending server connection.
    /// </summary>
    [RelayCommand]
    private void CancelServerSelection()
    {
        ShowAutoConnectDialog = false;
        PendingConnectionServer = null;
    }

    /// <summary>
    /// Connects to a discovered server by building the WebSocket URL.
    /// </summary>
    private async Task ConnectToDiscoveredServerAsync(DiscoveredServer server)
    {
        var ip = server.IpAddresses.FirstOrDefault() ?? server.Host;
        var path = server.Properties.TryGetValue("path", out var p) ? p : "/sendspin";
        ManualServerUrl = $"ws://{ip}:{server.Port}{path}";
        await ConnectToServerAsync();
    }

    /// <summary>
    /// Saves the auto-connect server preference to settings.
    /// </summary>
    private async Task SaveAutoConnectPreferenceAsync(string serverId)
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var connectionSection = root["Connection"]?.AsObject() ?? new JsonObject();
            connectionSection["AutoConnectServerId"] = serverId;
            root["Connection"] = connectionSection;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            _logger.LogInformation("Auto-connect preference saved for server: {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save auto-connect preference");
        }
    }

    /// <summary>
    /// Saves the current settings to user appsettings.json in AppData.
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            // Read existing settings or create new
            JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            // Update logging section
            var loggingSection = root["Logging"]?.AsObject() ?? new JsonObject();
            loggingSection["LogLevel"] = SettingsLogLevel;
            loggingSection["EnableFileLogging"] = SettingsEnableFileLogging;
            loggingSection["EnableConsoleLogging"] = SettingsEnableConsoleLogging;
            root["Logging"] = loggingSection;

            // Update audio section
            var audioSection = root["Audio"]?.AsObject() ?? new JsonObject();
            audioSection["StaticDelayMs"] = SettingsStaticDelayMs;
            audioSection["DeviceId"] = SettingsSelectedAudioDevice?.DeviceId;
            root["Audio"] = audioSection;

            // Update notifications section
            var notificationsSection = root["Notifications"]?.AsObject() ?? new JsonObject();
            notificationsSection["Enabled"] = SettingsShowNotifications;
            root["Notifications"] = notificationsSection;

            // Update Discord section
            var discordSection = root["Discord"]?.AsObject() ?? new JsonObject();
            discordSection["Enabled"] = SettingsShowDiscordPresence;
            root["Discord"] = discordSection;

            // Update player section
            var playerSection = root["Player"]?.AsObject() ?? new JsonObject();
            playerSection["Name"] = SettingsPlayerName;
            root["Player"] = playerSection;

            // Write back with nice formatting
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            // Apply notification setting immediately
            _notificationService.IsEnabled = SettingsShowNotifications;

            // Apply logging settings immediately (no restart required)
            App.Current.ReconfigureLogging(SettingsLogLevel, SettingsEnableFileLogging, SettingsEnableConsoleLogging);

            // Check if player name changed - requires reconnect to take effect
            var playerNameChanged = _clientCapabilities.ClientName != SettingsPlayerName;
            if (playerNameChanged)
            {
                _clientCapabilities.ClientName = SettingsPlayerName;
                _logger.LogInformation("Player name changed to: {PlayerName}", SettingsPlayerName);
            }

            _logger.LogInformation("Settings saved: LogLevel={LogLevel}, FileLogging={FileLogging}, ConsoleLogging={ConsoleLogging}, StaticDelayMs={StaticDelayMs}, Notifications={Notifications}, Discord={Discord}, PlayerName={PlayerName}, DeviceId={DeviceId}",
                SettingsLogLevel, SettingsEnableFileLogging, SettingsEnableConsoleLogging, SettingsStaticDelayMs, SettingsShowNotifications, SettingsShowDiscordPresence, SettingsPlayerName, SettingsSelectedAudioDevice?.DeviceId ?? "default");

            // Close settings panel first
            IsSettingsOpen = false;

            // If player name changed and we're connected, reconnect to apply the new name
            if (playerNameChanged && IsConnected)
            {
                StatusMessage = "Reconnecting with new player name...";
                _logger.LogInformation("Reconnecting to apply new player name");

                // Store the current server URL for reconnection
                var currentServerUrl = ManualServerUrl;

                // Disconnect
                await DisconnectFromServerAsync();

                // Small delay to ensure clean disconnection
                await Task.Delay(500);

                // Reconnect
                if (!string.IsNullOrEmpty(currentServerUrl))
                {
                    await ConnectToServerAsync();
                }

                StatusMessage = "Settings saved. Reconnected with new player name.";
            }
            else
            {
                StatusMessage = "Settings saved. Some changes require restart.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            SetError($"Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the log folder in Windows Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var logPath = LogFilePath;
            if (!Directory.Exists(logPath))
            {
                Directory.CreateDirectory(logPath);
            }

            System.Diagnostics.Process.Start("explorer.exe", logPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log folder");
            SetError($"Failed to open log folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Stats for Nerds diagnostic window.
    /// </summary>
    [RelayCommand]
    private void OpenStatsWindow()
    {
        try
        {
            var statsViewModel = new StatsViewModel(_audioPipeline, _clockSynchronizer);
            var statsWindow = new StatsWindow(statsViewModel)
            {
                Owner = App.Current.MainWindow,
            };
            statsWindow.Show();
            _logger.LogDebug("Stats window opened");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open stats window");
            SetError($"Failed to open stats window: {ex.Message}");
        }
    }

    #endregion

    #region System Tray Support

    /// <summary>
    /// Gets the tooltip text for the system tray icon.
    /// Shows connection status and current track if playing.
    /// </summary>
    public string TrayToolTip
    {
        get
        {
            if (!IsConnected)
                return "Sendspin - Disconnected";

            if (CurrentTrack != null && PlaybackState == PlaybackState.Playing)
                return $"Sendspin - {CurrentTrack.Title ?? "Unknown"}\nby {CurrentTrack.Artist ?? "Unknown"}";

            if (PlaybackState == PlaybackState.Paused)
                return "Sendspin - Paused";

            return $"Sendspin - Connected to {ConnectedServerName ?? "server"}";
        }
    }

    /// <summary>
    /// Command to show the main window.
    /// </summary>
    [RelayCommand]
    private void ShowWindow()
    {
        var mainWindow = App.Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Show();
            mainWindow.WindowState = System.Windows.WindowState.Normal;
            mainWindow.Activate();
        }
    }

    /// <summary>
    /// Command to hide the main window to system tray.
    /// </summary>
    [RelayCommand]
    private void HideWindow()
    {
        App.Current.MainWindow?.Hide();
    }

    /// <summary>
    /// Command to toggle main window visibility.
    /// Used for tray icon left-click.
    /// </summary>
    [RelayCommand]
    private void ToggleWindow()
    {
        var mainWindow = App.Current.MainWindow;
        if (mainWindow != null && mainWindow.IsVisible)
        {
            mainWindow.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// Command to exit the application completely.
    /// </summary>
    [RelayCommand]
    private void ExitApplication()
    {
        App.Current.Shutdown();
    }

    /// <summary>
    /// Updates tray tooltip when relevant properties change.
    /// </summary>
    private void UpdateTrayToolTip()
    {
        OnPropertyChanged(nameof(TrayToolTip));
    }

    /// <summary>
    /// Gets the application version from assembly metadata.
    /// </summary>
    private static string GetAppVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;

        // Try to get informational version (includes pre-release tags)
        var infoVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Strip the +hash suffix if present (e.g., "1.0.0+abc123" -> "1.0.0")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex > 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        return version?.ToString(3) ?? "Unknown";
    }

    #endregion

    public async Task ShutdownAsync()
    {
        _logger.LogInformation("Shutting down MainViewModel");

        // Stop position interpolation timer
        _positionTimer.Stop();

        // Cancel any pending volume changes
        _volumeDebouncesCts?.Cancel();
        _volumeDebouncesCts?.Dispose();
        _volumeDebouncesCts = null;

        // Cancel any pending static delay debounce
        _staticDelayClearCts?.Cancel();
        _staticDelayClearCts?.Dispose();
        _staticDelaySaveCts?.Cancel();
        _staticDelaySaveCts?.Dispose();

        // Stop server discovery
        await _serverDiscovery.StopAsync();

        // Cleanup manual client
        await CleanupManualClientAsync();
        _cleanupLock.Dispose();

        // Stop host service
        await _hostService.StopAsync();

        // Dispose Discord Rich Presence service (clears presence)
        _discordService.Dispose();

        // Note: HttpClient is managed by IHttpClientFactory, no manual disposal needed
    }
}
