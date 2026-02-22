using System.Windows;
using MacWinBridge.App.Services;
using MacWinBridge.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    internal BridgeOrchestrator? Orchestrator { get; private set; }
    internal BridgeConfig Config { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load configuration
        Config = BridgeConfig.Load();

        // Set up DI
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });
        services.AddSingleton(Config);

        _serviceProvider = services.BuildServiceProvider();

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        // Create orchestrator
        Orchestrator = new BridgeOrchestrator(
            loggerFactory.CreateLogger<BridgeOrchestrator>(),
            loggerFactory,
            Config);

        // Show main window
        var mainWindow = new MainWindow(this);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Orchestrator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
