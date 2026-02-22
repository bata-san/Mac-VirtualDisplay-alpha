using System;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.App;

/// <summary>
/// Microsoft.Extensions.Logging のパイプラインを AppLogger に流す。
/// </summary>
public sealed class AppLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new AppLoggerInstance(categoryName);

    public void Dispose() { }
}

internal sealed class AppLoggerInstance : ILogger
{
    private readonly string _category;

    public AppLoggerInstance(string category)
    {
        // "MacWinBridge.Core.BridgeOrchestrator" → "BridgeOrchestrator"
        _category = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;
    }

#pragma warning disable CS8633, CS8766
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
#pragma warning restore CS8633, CS8766

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace       => "TRACE",
            LogLevel.Debug       => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _                    => "?    "
        };

        var msg = formatter(state, exception);
        if (exception is not null)
            msg += $"  ({exception.GetType().Name}: {exception.Message})";

        AppLogger.Write(level, $"[{_category}] {msg}");
    }
}
