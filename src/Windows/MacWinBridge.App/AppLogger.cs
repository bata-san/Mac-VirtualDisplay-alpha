using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace MacWinBridge.App;

/// <summary>
/// シンプルなアプリログ。UI通知 + ファイル書き込みを提供する。
/// </summary>
public static class AppLogger
{
    private static readonly ConcurrentQueue<string> _lines = new();
    private const int MaxBufferedLines = 500;

    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();

    /// <summary>新しいログ行が追加されたときに発火 (UIスレッド外から呼ばれることがある)</summary>
    public static event Action<string>? LineAdded;

    static AppLogger()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MacWinBridge");
            Directory.CreateDirectory(dir);

            LogFilePath = Path.Combine(dir, $"bridge_{DateTime.Now:yyyyMMdd}.log");
            _fileWriter = new StreamWriter(LogFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };
            Write("INFO", "────────────────── セッション開始 ──────────────────");
        }
        catch
        {
            // ファイルログが使えなくても動作継続
        }
    }

    public static string LogFilePath { get; private set; } = string.Empty;

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    public static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

        _lines.Enqueue(line);
        while (_lines.Count > MaxBufferedLines)
            _lines.TryDequeue(out _);

        try
        {
            lock (_fileLock)
                _fileWriter?.WriteLine(line);
        }
        catch { }

        LineAdded?.Invoke(line);
    }

    public static string[] GetBufferedLines() => _lines.ToArray();
}
