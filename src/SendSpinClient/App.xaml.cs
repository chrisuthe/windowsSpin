using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SendSpinClient.Configuration;
using SendSpinClient.Core.Audio;
using SendSpinClient.Core.Client;
using SendSpinClient.Core.Discovery;
using SendSpinClient.Core.Models;
using SendSpinClient.Core.Synchronization;
using SendSpinClient.Services.Audio;
using SendSpinClient.Services.Notifications;
using SendSpinClient.ViewModels;
using Serilog;
using Serilog.Events;

namespace SendSpinClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IConfiguration? _configuration;
    private MainViewModel? _mainViewModel;
    private TaskbarIcon? _trayIcon;

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

        // Load configuration from appsettings.json
        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        // Configure Serilog from settings
        ConfigureSerilog(_configuration);

        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Create main window (but don't show it - starts in tray)
        var mainWindow = new MainWindow();
        _mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = _mainViewModel;
        MainWindow = mainWindow;

        // Set up system tray icon
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.DataContext = _mainViewModel;

        // Initialize the view model (app starts hidden in system tray)
        _ = _mainViewModel.InitializeAsync();
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
        services.AddSingleton<ClientCapabilities>();

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
        services.AddTransient<IAudioPlayer, WasapiAudioPlayer>();

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
        services.AddSingleton<SendSpinHostService>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var capabilities = sp.GetRequiredService<ClientCapabilities>();
            return new SendSpinHostService(loggerFactory, capabilities);
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

        // ViewModels
        services.AddTransient<MainViewModel>();
    }

    private static void ConfigureSerilog(IConfiguration configuration)
    {
        var settings = new LoggingSettings();
        configuration.GetSection(LoggingSettings.SectionName).Bind(settings);

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

            var logFilePath = Path.Combine(logDirectory, "sendspin-.log");

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
        Log.Information("SendSpin Client starting. Log level: {LogLevel}, File logging: {FileLogging}, Log directory: {LogDir}",
            settings.LogLevel,
            settings.EnableFileLogging,
            settings.EnableFileLogging ? settings.GetEffectiveLogDirectory() : "N/A");
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
        Log.Information("SendSpin Client shutting down");
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
                "SendSpin Client Error",
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
