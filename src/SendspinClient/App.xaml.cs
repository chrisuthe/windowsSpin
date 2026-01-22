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
using SendspinClient.Services.Diagnostics;
using SendspinClient.Services.Discord;
using SendspinClient.Services.Models;
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
        // Register configuration for components that need runtime access (e.g., settings UI)
        // Note: Most configuration is read at DI registration time below because the audio
        // services are singletons with immutable configuration - they don't support runtime
        // reconfiguration. This is intentional for a desktop app where services are created
        // once at startup. Using IOptions<T> would add complexity without benefit here.
        services.AddSingleton(_configuration!);

        // Logging - use Serilog with configuration from appsettings.json
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            // Pass logger: null so the provider dynamically accesses Log.Logger
            // This allows runtime log level changes via ReconfigureLogging() to take effect
            builder.AddSerilog(logger: null, dispose: true);
        });

        // Client capabilities configuration
        // Read player name from configuration (defaults to computer name)
        var playerName = _configuration!.GetValue<string>("Player:Name", Environment.MachineName) ?? Environment.MachineName;

        // Get app version for device info
        var appVersion = GetAppVersion();

        // Get persistent client ID (generated once per installation, survives reinstalls)
        var clientId = ClientIdService.GetOrCreateClientId();

        // Read audio device ID from configuration (null = system default)
        var audioDeviceId = _configuration!.GetValue<string?>("Audio:DeviceId");

        // Query device capabilities at startup to advertise supported hi-res formats
        var deviceCapabilities = WasapiAudioPlayer.QueryDeviceCapabilities(audioDeviceId);

        // Read preferred codec from configuration (default: flac for lossless quality)
        var preferredCodec = _configuration!.GetValue<string>("Audio:PreferredCodec", "flac")?.ToLowerInvariant() ?? "flac";

        // Build audio formats based on device capabilities
        var audioFormats = AudioFormatBuilder.BuildFormats(deviceCapabilities, preferredCodec);

        Log.Information(
            "Audio capabilities: {SampleRate}Hz {BitDepth}-bit, advertising {FormatCount} formats (preferred: {Codec})",
            deviceCapabilities.NativeSampleRate,
            deviceCapabilities.NativeBitDepth,
            audioFormats.Count,
            preferredCodec.ToUpperInvariant());

        services.AddSingleton(new ClientCapabilities
        {
            ClientId = clientId,
            ClientName = playerName,
            ProductName = "Sendspin Windows Client",
            Manufacturer = null, // Set by SDK consumers as needed
            SoftwareVersion = appVersion,
            AudioFormats = audioFormats
        });

        // Clock synchronization for multi-room audio sync
        services.AddSingleton<IClockSynchronizer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<KalmanClockSynchronizer>>();

            // Read Kalman filter configuration (all have sensible defaults)
            var forgetFactor = _configuration!.GetValue<double>("Audio:ClockSync:ForgetFactor", 1.001);
            var adaptiveCutoff = _configuration!.GetValue<double>("Audio:ClockSync:AdaptiveCutoff", 0.75);
            var minSamplesForForgetting = _configuration!.GetValue<int>("Audio:ClockSync:MinSamplesForForgetting", 100);

            var clockSync = new KalmanClockSynchronizer(
                logger,
                forgetFactor: forgetFactor,
                adaptiveCutoff: adaptiveCutoff,
                minSamplesForForgetting: minSamplesForForgetting);

            // Apply static delay from configuration
            var staticDelayMs = _configuration!.GetValue<double>("Audio:StaticDelayMs", 0);
            clockSync.StaticDelayMs = staticDelayMs;

            return clockSync;
        });

        // Audio pipeline components
        services.AddSingleton<IAudioDecoderFactory, AudioDecoderFactory>();

        // Read sync correction strategy configuration
        var strategyStr = _configuration!.GetValue<string>("Audio:SyncCorrection:Strategy", "Combined");
        var syncStrategy = strategyStr?.Equals("DropInsertOnly", StringComparison.OrdinalIgnoreCase) == true
            ? SyncCorrectionStrategy.DropInsertOnly
            : SyncCorrectionStrategy.Combined;
        var resamplerTypeStr = _configuration!.GetValue<string>("Audio:SyncCorrection:ResamplerType", "Wdl");
        var resamplerType = resamplerTypeStr?.Equals("SoundTouch", StringComparison.OrdinalIgnoreCase) == true
            ? ResamplerType.SoundTouch
            : ResamplerType.Wdl;

        // Read diagnostics configuration
        var diagnosticsSettings = new DiagnosticsSettings();
        _configuration!.GetSection(DiagnosticsSettings.SectionName).Bind(diagnosticsSettings);

        // Diagnostic audio recorder for capturing audio with sync metrics
        services.AddSingleton<IDiagnosticAudioRecorder>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DiagnosticAudioRecorder>>();
            return new DiagnosticAudioRecorder(logger, diagnosticsSettings.BufferSeconds);
        });

        services.AddTransient<IAudioPlayer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WasapiAudioPlayer>>();
            var diagnosticRecorder = sp.GetRequiredService<IDiagnosticAudioRecorder>();
            return new WasapiAudioPlayer(logger, audioDeviceId, syncStrategy, resamplerType, diagnosticRecorder);
        });

        // Audio pipeline - orchestrates decoder, buffer, and player
        services.AddSingleton<IAudioPipeline>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AudioPipeline>>();
            var bufferLogger = sp.GetRequiredService<ILogger<TimedAudioBuffer>>();
            var decoderFactory = sp.GetRequiredService<IAudioDecoderFactory>();
            var clockSync = sp.GetRequiredService<IClockSynchronizer>();

            // Read buffer configuration (matching JS client's faster startup)
            var bufferTargetMs = _configuration!.GetValue<double>("Audio:Buffer:TargetMs", 250);
            var bufferCapacityMs = _configuration!.GetValue<int>("Audio:Buffer:CapacityMs", 8000);

            // Read clock sync wait configuration
            var waitForConvergence = _configuration!.GetValue<bool>("Audio:ClockSync:WaitForConvergence", true);
            var convergenceTimeoutMs = _configuration!.GetValue<int>("Audio:ClockSync:ConvergenceTimeoutMs", 5000);

            return new AudioPipeline(
                logger,
                decoderFactory,
                clockSync,
                bufferFactory: (format, sync) =>
                {
                    var buffer = new TimedAudioBuffer(format, sync, bufferCapacityMs, syncOptions: null, bufferLogger);
                    buffer.TargetBufferMilliseconds = bufferTargetMs;
                    return buffer;
                },
                playerFactory: () => sp.GetRequiredService<IAudioPlayer>(),
                sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(buffer, timeFunc),
                precisionTimer: null,
                waitForConvergence: waitForConvergence,
                convergenceTimeoutMs: convergenceTimeoutMs);
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

        // User settings service for persisting runtime configuration changes
        services.AddSingleton<IUserSettingsService, UserSettingsService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
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

            // Save the settings synchronously so they persist before MainViewModel loads
            SaveLoggingSettingsAsync("Warning", enableFileLogging: false, enableConsoleLogging: false).GetAwaiter().GetResult();

            // Reload configuration so MainViewModel sees the updated values
            // This is necessary because the IConfiguration was built before the dialog saved
            ((IConfigurationRoot)_configuration!).Reload();
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
                var json = await File.ReadAllTextAsync(appSettingsPath).ConfigureAwait(false);
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
            await File.WriteAllTextAsync(appSettingsPath, updatedJson).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors - settings won't persist but logging is already disabled
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // Dispose tray icon to remove from system tray
            _trayIcon?.Dispose();

            // Gracefully shutdown the host service
            if (_mainViewModel != null)
            {
                try
                {
                    await _mainViewModel.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    // Log using Serilog's static logger (still available at this point)
                    Log.Error(ex, "Error during MainViewModel shutdown");
                }
            }

            // Dispose service provider (may throw during service disposal)
            try
            {
                _serviceProvider?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error disposing service provider");
            }

            // Flush and close Serilog to ensure all logs are written
            Log.Information("WindowsSpin shutting down");
            try
            {
                await Log.CloseAndFlushAsync();
            }
            catch
            {
                // Ignore - nothing we can do if log flush fails, and no logger available
            }
        }
        catch (Exception ex)
        {
            // Last resort catch for any unexpected exceptions
            // Use Debug.WriteLine since logger may be unavailable
            System.Diagnostics.Debug.WriteLine($"Unexpected error during shutdown: {ex}");
        }
        finally
        {
            // Always call base.OnExit to ensure proper WPF shutdown
            base.OnExit(e);
        }
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
}
