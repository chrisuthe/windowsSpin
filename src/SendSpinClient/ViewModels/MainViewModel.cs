using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Client;
using SendSpinClient.Core.Connection;
using SendSpinClient.Core.Discovery;
using SendSpinClient.Core.Models;
using SendSpinClient.Core.Protocol.Messages;
using SendSpinClient.Core.Synchronization;

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
    private readonly SendSpinHostService _hostService;
    private readonly MdnsServerDiscovery _serverDiscovery;
    private readonly IAudioPipeline _audioPipeline;
    private readonly HttpClient _httpClient;
    private SendSpinClientService? _manualClient;
    private ISendSpinConnection? _manualConnection;
    private string? _lastArtworkUrl;
    private string? _autoConnectedServerId;
    private bool _autoReconnectEnabled = true;

    [ObservableProperty]
    private bool _isHosting;

    [ObservableProperty]
    private string? _clientId;

    [ObservableProperty]
    private string? _connectedServerName;

    [ObservableProperty]
    private TrackMetadata? _currentTrack;

    [ObservableProperty]
    private PlaybackState _playbackState = PlaybackState.Idle;

    [ObservableProperty]
    private int _volume = 100;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private double _duration;

    [ObservableProperty]
    private byte[]? _albumArtwork;

    [ObservableProperty]
    private string _statusMessage = "Starting...";

    [ObservableProperty]
    private string _manualServerUrl = "ws://10.0.2.8:8927/sendspin";

    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>
    /// List of connected SendSpin servers.
    /// </summary>
    public ObservableCollection<ConnectedServerInfo> ConnectedServers { get; } = new();

    public bool IsConnected => ConnectedServers.Count > 0 || _manualClient?.ConnectionState == ConnectionState.Connected;
    public bool IsPlaying => PlaybackState == PlaybackState.Playing;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
        SendSpinHostService hostService,
        MdnsServerDiscovery serverDiscovery,
        IAudioPipeline audioPipeline)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _hostService = hostService;
        _serverDiscovery = serverDiscovery;
        _audioPipeline = audioPipeline;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);

        // Subscribe to host events (server-initiated mode fallback)
        _hostService.ServerConnected += OnServerConnected;
        _hostService.ServerDisconnected += OnServerDisconnected;
        _hostService.GroupStateChanged += OnGroupStateChanged;
        _hostService.ArtworkReceived += OnArtworkReceived;

        // Subscribe to server discovery events (client-initiated mode - primary)
        _serverDiscovery.ServerFound += OnDiscoveredServerFound;
        _serverDiscovery.ServerLost += OnDiscoveredServerLost;
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
            await _hostService.SendCommandAsync(command);
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
            await _hostService.SendCommandAsync(Commands.Next);
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
            await _hostService.SendCommandAsync(Commands.Previous);
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
            await _hostService.SendCommandAsync(Commands.SetMute, new Dictionary<string, object>
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

            // Create the connection and client service
            _manualConnection = new SendSpinConnection(
                _loggerFactory.CreateLogger<SendSpinConnection>());

            _manualClient = new SendSpinClientService(
                _loggerFactory.CreateLogger<SendSpinClientService>(),
                _manualConnection,
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
                _ = CleanupManualClientAsync();
            }
        });
    }

    private void OnManualClientGroupStateChanged(object? sender, GroupState group)
    {
        App.Current.Dispatcher.Invoke(() =>
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
                _ = FetchArtworkAsync(artworkUrl);
            }

            _logger.LogDebug("Manual client group state updated: {State}", group.PlaybackState);
        });
    }

    private async Task FetchArtworkAsync(string url)
    {
        try
        {
            _logger.LogDebug("Fetching artwork from {Url}", url);
            var imageData = await _httpClient.GetByteArrayAsync(url);

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

            _logger.LogInformation("Server connected: {ServerName} ({ServerId})",
                server.ServerName, server.ServerId);
        });
    }

    private void OnServerDisconnected(object? sender, string serverId)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var server = ConnectedServers.FirstOrDefault(s => s.ServerId == serverId);
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
                _ = FetchArtworkAsync(artworkUrl);
            }

            _logger.LogDebug("Group state updated: {State}, Track: {Track}",
                group.PlaybackState, group.Metadata?.ToString());
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
                _ = AutoConnectToServerAsync(server);
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

    partial void OnPlaybackStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(IsPlaying));
    }

    public async Task ShutdownAsync()
    {
        _logger.LogInformation("Shutting down MainViewModel");

        // Stop server discovery
        await _serverDiscovery.StopAsync();

        // Cleanup manual client
        await CleanupManualClientAsync();

        // Stop host service
        await _hostService.StopAsync();
    }
}
