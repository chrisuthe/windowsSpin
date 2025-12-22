using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Client;
using SendSpinClient.Core.Discovery;
using SendSpinClient.Core.Models;
using SendSpinClient.ViewModels;

namespace SendSpinClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private MainViewModel? _mainViewModel;

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

        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Create and show main window
        var mainWindow = new MainWindow();
        _mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = _mainViewModel;
        mainWindow.Show();

        // Initialize the view model
        _ = _mainViewModel.InitializeAsync();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging - enable console output for debugging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddConsole();
        });

        // Client capabilities configuration
        services.AddSingleton<ClientCapabilities>();

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

        // ViewModels
        services.AddTransient<MainViewModel>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Gracefully shutdown the host service
        if (_mainViewModel != null)
        {
            await _mainViewModel.ShutdownAsync();
        }

        _serviceProvider?.Dispose();
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
