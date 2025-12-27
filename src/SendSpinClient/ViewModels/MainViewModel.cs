using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendSpinClient.Configuration;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Client;
using SendSpinClient.Core.Connection;
using SendSpinClient.Core.Discovery;
using SendSpinClient.Core.Extensions;
using SendSpinClient.Core.Models;
using SendSpinClient.Core.Protocol.Messages;
using SendSpinClient.Core.Synchronization;
using SendSpinClient.Services.Notifications;
using SendSpinClient.Views;

namespace SendSpinClient.ViewModels;

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
    private readonly SendSpinHostService _hostService;
    private readonly MdnsServerDiscovery _serverDiscovery;
    private readonly IAudioPipeline _audioPipeline;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly INotificationService _notificationService;
    private readonly HttpClient _httpClient;
    private SendSpinClientService? _manualClient;
    private ISendSpinConnection? _manualConnection;
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
    /// Gets or sets the display name of the currently connected SendSpin server.
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
    /// Gets the path where log files are stored.
    /// </summary>
    public string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SendSpin",
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
    /// Gets the collection of currently connected SendSpin servers.
    /// Updated when servers connect or disconnect via the host service.
    /// </summary>
    public ObservableCollection<ConnectedServerInfo> ConnectedServers { get; } = new();

    /// <summary>
    /// Gets whether the client is connected to any SendSpin server,
    /// either via manual connection or through the host service.
    /// </summary>
    public bool IsConnected => ConnectedServers.Count > 0 || _manualClient?.ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// Gets whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => PlaybackState == PlaybackState.Playing;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        SendSpinHostService hostService,
        MdnsServerDiscovery serverDiscovery,
        IAudioPipeline audioPipeline,
        IClockSynchronizer clockSynchronizer,
        INotificationService notificationService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _hostService = hostService;
        _serverDiscovery = serverDiscovery;
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        _notificationService = notificationService;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);

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
            StatusMessage = "Discovering SendSpin servers...";
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
            await SendCommandToActiveClientAsync(Commands.Mute, new Dictionary<string, object>
            {
                ["muted"] = !IsMuted
            });
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
    /// Manually connect to a SendSpin server by URL (for cross-subnet scenarios).
    /// </summary>
    [RelayCommand]
    private async Task ConnectToServerAsync()
    {
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
            // Parse the URL
            if (!Uri.TryCreate(ManualServerUrl, UriKind.Absolute, out var serverUri))
            {
                StatusMessage = "Invalid URL format";
                IsConnecting = false;
                return;
            }

            // Validate WebSocket scheme
            if (serverUri.Scheme != "ws" && serverUri.Scheme != "wss")
            {
                StatusMessage = "URL must start with ws:// or wss://";
                IsConnecting = false;
                return;
            }

            // Create the connection and client service
            _manualConnection = new SendSpinConnection(
                _loggerFactory.CreateLogger<SendSpinConnection>());

            _manualClient = new SendSpinClientService(
                _loggerFactory.CreateLogger<SendSpinClientService>(),
                _manualConnection,
                clockSynchronizer: _clockSynchronizer,
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
        if (_manualClient != null)
        {
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
            var imageData = await _httpClient.GetByteArrayAsync(artworkUri);

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

        // Auto-connect if not already connected
        if (!IsConnected && _autoReconnectEnabled)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                AutoConnectToServerAsync(server).SafeFireAndForget(_logger);
            });
        }
    }

    private void OnDiscoveredServerLost(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Server lost: {Name} ({ServerId})", server.Name, server.ServerId);

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
            _manualConnection = new SendSpinConnection(
                _loggerFactory.CreateLogger<SendSpinConnection>());

            _manualClient = new SendSpinClientService(
                _loggerFactory.CreateLogger<SendSpinClientService>(),
                _manualConnection,
                clockSynchronizer: _clockSynchronizer,
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
        }
        else if (value == null)
        {
            // Track cleared - reset artwork
            AlbumArtwork = null;
            _lastArtworkUrl = null;
            _previousTrackId = null;
        }
    }

    partial void OnConnectedServerNameChanged(string? value)
    {
        UpdateTrayToolTip();
    }

    private CancellationTokenSource? _staticDelayClearCts;
    private CancellationTokenSource? _staticDelaySaveCts;

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
    /// Saves only the static delay setting to appsettings.json.
    /// </summary>
    private async Task SaveStaticDelayAsync(double value)
    {
        try
        {
            var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

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
            await SendCommandToActiveClientAsync(Commands.Volume, new Dictionary<string, object>
            {
                ["volume"] = volume
            });
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
    /// Saves the current settings to appsettings.json.
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

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
            root["Audio"] = audioSection;

            // Write back with nice formatting
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);

            _logger.LogInformation("Settings saved: LogLevel={LogLevel}, FileLogging={FileLogging}, ConsoleLogging={ConsoleLogging}, StaticDelayMs={StaticDelayMs}",
                SettingsLogLevel, SettingsEnableFileLogging, SettingsEnableConsoleLogging, SettingsStaticDelayMs);

            // Show success and close settings
            // Static delay takes effect immediately; logging changes require restart
            StatusMessage = "Settings saved. Logging changes require restart.";
            IsSettingsOpen = false;
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
                return "SendSpin - Disconnected";

            if (CurrentTrack != null && PlaybackState == PlaybackState.Playing)
                return $"SendSpin - {CurrentTrack.Title ?? "Unknown"}\nby {CurrentTrack.Artist ?? "Unknown"}";

            if (PlaybackState == PlaybackState.Paused)
                return "SendSpin - Paused";

            return $"SendSpin - Connected to {ConnectedServerName ?? "server"}";
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

    #endregion

    public async Task ShutdownAsync()
    {
        _logger.LogInformation("Shutting down MainViewModel");

        // Cancel any pending volume changes
        _volumeDebouncesCts?.Cancel();
        _volumeDebouncesCts?.Dispose();
        _volumeDebouncesCts = null;

        // Stop server discovery
        await _serverDiscovery.StopAsync();

        // Cleanup manual client
        await CleanupManualClientAsync();

        // Stop host service
        await _hostService.StopAsync();

        // Dispose HTTP client
        _httpClient?.Dispose();
    }
}
