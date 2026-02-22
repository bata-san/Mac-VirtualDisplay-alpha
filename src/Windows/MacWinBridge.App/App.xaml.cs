using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

        // ── グローバル例外ハンドリング ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Load configuration
        Config = BridgeConfig.Load();

        // Set up DI
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
            builder.AddProvider(new AppLoggerProvider());   // UIログパネルへ転送
        });
        services.AddSingleton(Config);

        _serviceProvider = services.BuildServiceProvider();

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        // Create orchestrator
        Orchestrator = new BridgeOrchestrator(
            loggerFactory.CreateLogger<BridgeOrchestrator>(),
            loggerFactory,
            Config);

        // StatusMessage も AppLogger へ
        Orchestrator.StatusMessage += (_, msg) => AppLogger.Info(msg);

        // Show main window
        var mainWindow = new MainWindow(this);
        mainWindow.Show();

        AppLogger.Info("Mac-Win Bridge 起動完了");
    }

    private void OnDispatcherUnhandledException(object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error($"UI例外: {e.Exception.GetType().Name}: {e.Exception.Message}");
        e.Handled = true;  // アプリを落とさない
    }

    private void OnUnobservedTaskException(object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error($"Task例外: {e.Exception?.InnerException?.Message ?? e.Exception?.Message}");
        e.SetObserved();
    }

    private void OnUnhandledException(object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLogger.Error($"致命的例外: {ex.GetType().Name}: {ex.Message}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Orchestrator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
