using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SendspinClient.Configuration;
using SendspinClient.Views;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using SendspinClient.Services.Audio;
using SendspinClient.Services.Discord;
using SendspinClient.Services.Notifications;
using SendspinClient.ViewModels;
using Serilog;
using Serilog.Events;

namespace SendspinClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IConfiguration? _configuration;
    private MainViewModel? _mainViewModel;
    private TaskbarIcon? _trayIcon;
    private LoggingSettings _currentLoggingSettings = new();

    /// <summary>
    /// Gets the current application instance.
    /// </summary>
    public new static App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public IServiceProvider Services => _serviceProvider!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize user settings directory (copy defaults on first run)
        AppPaths.InitializeUserSettingsIfNeeded();

        // Load configuration: first from install directory (defaults), then from user AppData (overrides)
        // This ensures user can always save settings even when installed to Program Files
        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppPaths.InstallDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(AppPaths.UserSettingsPath, optional: true, reloadOnChange: true)
            .Build();

        // Configure Serilog from settings
        ConfigureSerilog(_configuration);

        // Check for verbose logging and show warning if needed
        CheckVerboseLoggingOnStartup();

        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Create main window
        var mainWindow = new MainWindow();
        _mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = _mainViewModel;
        MainWindow = mainWindow;

        // Set up system tray icon
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.DataContext = _mainViewModel;

        // Check if this is the first launch - show welcome screen if so
        var hasLaunchedBefore = _configuration!.GetValue<bool>("App:HasLaunchedBefore", false);
        if (!hasLaunchedBefore)
        {
            // First launch - show the welcome screen
            mainWindow.Show();

            // Mark as launched (save immediately to prevent showing again if app crashes)
            _ = SaveFirstLaunchFlagAsync();
        }
        // else: stays hidden in system tray (normal behavior)

        // Initialize the view model
        _ = _mainViewModel.InitializeAsync();
    }

    /// <summary>
    /// Saves the first launch flag to settings so the welcome screen is only shown once.
    /// </summary>
    private static async Task SaveFirstLaunchFlagAsync()
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            System.Text.Json.Nodes.JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = System.Text.Json.Nodes.JsonNode.Parse(json) ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            var appSection = root["App"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            appSection["HasLaunchedBefore"] = true;
            root["App"] = appSection;

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);
        }
        catch
        {
            // Ignore errors - next launch will just show welcome again
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register configuration
        services.AddSingleton(_configuration!);
        services.Configure<LoggingSettings>(_configuration!.GetSection(LoggingSettings.SectionName));

        // Logging - use Serilog with configuration from appsettings.json
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // Client capabilities configuration
        // Read player name from configuration (defaults to computer name)
        var playerName = _configuration!.GetValue<string>("Player:Name", Environment.MachineName) ?? Environment.MachineName;
        services.AddSingleton(new ClientCapabilities { ClientName = playerName });

        // Clock synchronization for multi-room audio sync
        services.AddSingleton<IClockSynchronizer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<KalmanClockSynchronizer>>();
            var clockSync = new KalmanClockSynchronizer(logger);

            // Apply static delay from configuration
            var staticDelayMs = _configuration!.GetValue<double>("Audio:StaticDelayMs", 0);
            clockSync.StaticDelayMs = staticDelayMs;

            return clockSync;
        });

        // Audio pipeline components
        services.AddSingleton<IAudioDecoderFactory, AudioDecoderFactory>();

        // Read audio device ID from configuration (null = system default)
        var audioDeviceId = _configuration!.GetValue<string?>("Audio:DeviceId");
        services.AddTransient<IAudioPlayer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WasapiAudioPlayer>>();
            return new WasapiAudioPlayer(logger, audioDeviceId);
        });

        // Audio pipeline - orchestrates decoder, buffer, and player
        services.AddSingleton<IAudioPipeline>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AudioPipeline>>();
            var decoderFactory = sp.GetRequiredService<IAudioDecoderFactory>();
            var clockSync = sp.GetRequiredService<IClockSynchronizer>();

            return new AudioPipeline(
                logger,
                decoderFactory,
                clockSync,
                // Use 8 second buffer to match Python CLI's unbounded queue approach
                // Server sends audio ~5 seconds ahead, so we need room to accumulate
                // without constant overflow. Sync correction handles buffer depth.
                bufferFactory: (format, sync) => new TimedAudioBuffer(format, sync, bufferCapacityMs: 8000),
                playerFactory: () => sp.GetRequiredService<IAudioPlayer>(),
                sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(buffer, timeFunc));
        });

        // Server discovery for client-initiated mode
        // Discovers Music Assistant servers via mDNS and auto-connects
        services.AddSingleton<MdnsServerDiscovery>();

        // Host service for server-initiated mode (backup/fallback)
        // The host runs a WebSocket server and advertises via mDNS
        // Music Assistant servers discover and connect to us
        services.AddSingleton<SendspinHostService>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var capabilities = sp.GetRequiredService<ClientCapabilities>();
            return new SendspinHostService(loggerFactory, capabilities);
        });

        // HTTP client factory for proper HttpClient lifecycle management
        // Named client "Artwork" is used for fetching album artwork
        services.AddHttpClient("Artwork", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Notification service for toast notifications
        // Uses Windows Toast API via Microsoft.Toolkit.Uwp.Notifications
        services.AddSingleton<INotificationService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WindowsToastNotificationService>>();

            // Callback to check if main window is visible
            // Used to suppress notifications when the app is in the foreground
            bool IsWindowVisible()
            {
                try
                {
                    return Current.Dispatcher.Invoke(() =>
                    {
                        var window = Current.MainWindow;
                        return window != null && window.IsVisible && window.IsActive;
                    });
                }
                catch
                {
                    return false;
                }
            }

            return new WindowsToastNotificationService(logger, IsWindowVisible);
        });

        // Discord Rich Presence service
        // Displays currently playing track in user's Discord activity status
        var discordAppId = _configuration!.GetValue<string>("Discord:ApplicationId") ?? string.Empty;
        services.AddSingleton<IDiscordRichPresenceService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DiscordRichPresenceService>>();
            return new DiscordRichPresenceService(logger, discordAppId);
        });

        // ViewModels
        services.AddTransient<MainViewModel>();
    }

    private void ConfigureSerilog(IConfiguration configuration)
    {
        var settings = new LoggingSettings();
        configuration.GetSection(LoggingSettings.SectionName).Bind(settings);

        // Store current settings for later use
        _currentLoggingSettings = settings;

        // Parse log level from configuration
        var logLevel = settings.LogLevel?.ToLowerInvariant() switch
        {
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .Enrich.FromLogContext();

        // Always add debug output for development
        logConfig.WriteTo.Debug(outputTemplate: settings.OutputTemplate);

        // Console logging (disabled by default for WPF)
        if (settings.EnableConsoleLogging)
        {
            logConfig.WriteTo.Console(outputTemplate: settings.OutputTemplate);
        }

        // File logging with rotation
        if (settings.EnableFileLogging)
        {
            var logDirectory = settings.GetEffectiveLogDirectory();
            Directory.CreateDirectory(logDirectory);

            var logFilePath = Path.Combine(logDirectory, "windowsspin-.log");

            logConfig.WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: settings.MaxFileSizeMB * 1024 * 1024,
                retainedFileCountLimit: settings.RetainedFileCount,
                rollOnFileSizeLimit: true,
                outputTemplate: settings.OutputTemplate,
                shared: true);
        }

        Log.Logger = logConfig.CreateLogger();
        Log.Information("WindowsSpin starting. Log level: {LogLevel}, File logging: {FileLogging}, Log directory: {LogDir}",
            settings.LogLevel,
            settings.EnableFileLogging,
            settings.EnableFileLogging ? settings.GetEffectiveLogDirectory() : "N/A");
    }

    /// <summary>
    /// Checks if verbose logging is enabled on startup and shows a warning dialog.
    /// </summary>
    private void CheckVerboseLoggingOnStartup()
    {
        if (!IsVerboseLoggingEnabled())
            return;

        var dialog = new LoggingWarningDialog();
        var result = dialog.ShowDialog();

        if (result == true && dialog.DisableLogging)
        {
            // Disable logging immediately
            ReconfigureLogging("Warning", enableFileLogging: false, enableConsoleLogging: false);

            // Also save the settings so they persist
            _ = SaveLoggingSettingsAsync("Warning", enableFileLogging: false, enableConsoleLogging: false);
        }
    }

    /// <summary>
    /// Checks if logging is configured at a verbose level (more verbose than Warning)
    /// and either file or console logging is enabled.
    /// </summary>
    private bool IsVerboseLoggingEnabled()
    {
        var logLevel = _currentLoggingSettings.LogLevel?.ToLowerInvariant() ?? "information";
        var isVerbose = logLevel is "verbose" or "trace" or "debug" or "information" or "info";
        var hasOutput = _currentLoggingSettings.EnableFileLogging || _currentLoggingSettings.EnableConsoleLogging;

        return isVerbose && hasOutput;
    }

    /// <summary>
    /// Reconfigures Serilog at runtime with new settings.
    /// This allows logging changes to take effect without restarting the app.
    /// </summary>
    /// <param name="logLevel">The new log level (Verbose, Debug, Information, Warning, Error, Fatal)</param>
    /// <param name="enableFileLogging">Whether to enable file logging</param>
    /// <param name="enableConsoleLogging">Whether to enable console logging</param>
    public void ReconfigureLogging(string logLevel, bool enableFileLogging, bool enableConsoleLogging)
    {
        // Update current settings
        _currentLoggingSettings.LogLevel = logLevel;
        _currentLoggingSettings.EnableFileLogging = enableFileLogging;
        _currentLoggingSettings.EnableConsoleLogging = enableConsoleLogging;

        // Parse log level
        var level = logLevel?.ToLowerInvariant() switch
        {
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext();

        // Always add debug output for development
        logConfig.WriteTo.Debug(outputTemplate: _currentLoggingSettings.OutputTemplate);

        // Console logging
        if (enableConsoleLogging)
        {
            logConfig.WriteTo.Console(outputTemplate: _currentLoggingSettings.OutputTemplate);
        }

        // File logging with rotation
        if (enableFileLogging)
        {
            var logDirectory = _currentLoggingSettings.GetEffectiveLogDirectory();
            Directory.CreateDirectory(logDirectory);

            var logFilePath = Path.Combine(logDirectory, "windowsspin-.log");

            logConfig.WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: _currentLoggingSettings.MaxFileSizeMB * 1024 * 1024,
                retainedFileCountLimit: _currentLoggingSettings.RetainedFileCount,
                rollOnFileSizeLimit: true,
                outputTemplate: _currentLoggingSettings.OutputTemplate,
                shared: true);
        }

        // Replace the global logger
        Log.Logger = logConfig.CreateLogger();
        Log.Information("Logging reconfigured. Level: {LogLevel}, File: {FileLogging}, Console: {ConsoleLogging}",
            logLevel, enableFileLogging, enableConsoleLogging);
    }

    /// <summary>
    /// Saves logging settings to the user's appsettings.json file.
    /// </summary>
    private static async Task SaveLoggingSettingsAsync(string logLevel, bool enableFileLogging, bool enableConsoleLogging)
    {
        try
        {
            AppPaths.EnsureUserDataDirectoryExists();
            var appSettingsPath = AppPaths.UserSettingsPath;

            System.Text.Json.Nodes.JsonNode? root;
            if (File.Exists(appSettingsPath))
            {
                var json = await File.ReadAllTextAsync(appSettingsPath);
                root = System.Text.Json.Nodes.JsonNode.Parse(json) ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            var loggingSection = root["Logging"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            loggingSection["LogLevel"] = logLevel;
            loggingSection["EnableFileLogging"] = enableFileLogging;
            loggingSection["EnableConsoleLogging"] = enableConsoleLogging;
            root["Logging"] = loggingSection;

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(appSettingsPath, updatedJson);
        }
        catch
        {
            // Ignore errors - settings won't persist but logging is already disabled
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Dispose tray icon to remove from system tray
        _trayIcon?.Dispose();

        // Gracefully shutdown the host service
        if (_mainViewModel != null)
        {
            await _mainViewModel.ShutdownAsync();
        }

        _serviceProvider?.Dispose();

        // Flush and close Serilog to ensure all logs are written
        Log.Information("WindowsSpin shutting down");
        await Log.CloseAndFlushAsync();

        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var logger = _serviceProvider?.GetService<ILogger<App>>();
        var exception = e.ExceptionObject as Exception;
        logger?.LogCritical(exception, "Unhandled exception");

        if (e.IsTerminating)
        {
            MessageBox.Show(
                $"A fatal error occurred:\n\n{exception?.Message}\n\nThe application will close.",
                "WindowsSpin Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var logger = _serviceProvider?.GetService<ILogger<App>>();
        logger?.LogError(e.Exception, "Dispatcher unhandled exception");

        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}",
            "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var logger = _serviceProvider?.GetService<ILogger<App>>();
        logger?.LogWarning(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
