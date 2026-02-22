using System.Windows;
using System.Windows.Media;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Display.Monitor;

namespace MacWinBridge.App;

public partial class MainWindow : Window
{
    private readonly App _app;
    private System.Windows.Threading.DispatcherTimer? _statsTimer;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        MacHostInput.Text = _app.Config.MacHost;
        UpdateDisplayModeUI(_app.Config.Display.Mode);
        DetectMonitors();

        // Wire up orchestrator events
        if (_app.Orchestrator is not null)
        {
            _app.Orchestrator.StatusMessage += (_, msg) =>
                Dispatcher.Invoke(() => StatusText.Text = msg);

            _app.Orchestrator.ConnectionChanged += (_, connected) =>
                Dispatcher.Invoke(() => OnConnectionChanged(connected));
        }

        // Stats refresh timer
        _statsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
    }

    private void DetectMonitors()
    {
        try
        {
            var monitors = MonitorManager.GetMonitors();
            MonitorCountText.Text = $"モニター: {monitors.Count}台検出";
        }
        catch
        {
            MonitorCountText.Text = "モニター: 検出失敗";
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_app.Orchestrator is null) return;

        if (_app.Orchestrator.IsConnected)
        {
            await _app.Orchestrator.DisconnectAsync();
            ConnectButton.Content = "接続";
            return;
        }

        // Save host config
        _app.Config.MacHost = MacHostInput.Text.Trim();
        _app.Config.Save();

        ConnectButton.Content = "接続中...";
        ConnectButton.IsEnabled = false;

        await _app.Orchestrator.ConnectAsync();

        ConnectButton.IsEnabled = true;
        ConnectButton.Content = _app.Orchestrator.IsConnected ? "切断" : "接続";
    }

    private async void OnWindowsModeClick(object sender, RoutedEventArgs e)
    {
        if (_app.Orchestrator is null) return;
        await _app.Orchestrator.SwitchDisplayModeAsync(DisplayMode.Windows);
        UpdateDisplayModeUI(DisplayMode.Windows);
    }

    private async void OnMacModeClick(object sender, RoutedEventArgs e)
    {
        if (_app.Orchestrator is null) return;
        await _app.Orchestrator.SwitchDisplayModeAsync(DisplayMode.Mac);
        UpdateDisplayModeUI(DisplayMode.Mac);
    }

    private void UpdateDisplayModeUI(DisplayMode mode)
    {
        DisplayModeText.Text = mode switch
        {
            DisplayMode.Mac => "現在: Macモード 🍎",
            DisplayMode.Windows => "現在: Windowsモード 🪟",
            _ => "不明",
        };

        // Highlight active mode button
        WindowsModeButton.Background = mode == DisplayMode.Windows
            ? (SolidColorBrush)FindResource("SuccessBrush")
            : (SolidColorBrush)FindResource("PrimaryBrush");

        MacModeButton.Background = mode == DisplayMode.Mac
            ? (SolidColorBrush)FindResource("SuccessBrush")
            : (SolidColorBrush)FindResource("PrimaryBrush");
    }

    private void OnConnectionChanged(bool connected)
    {
        ConnectButton.Content = connected ? "切断" : "接続";

        var connectedBrush = connected
            ? (SolidColorBrush)FindResource("SuccessBrush")
            : (SolidColorBrush)FindResource("DangerBrush");

        AudioStatusText.Text = connected ? "ストリーミング中" : "停止";
        AudioStatusText.Foreground = connectedBrush;

        KvmStatusText.Text = connected ? "アクティブ" : "停止";
        KvmStatusText.Foreground = connectedBrush;

        ConnectionInfo.Text = connected
            ? $"接続先: {_app.Orchestrator?.ConnectedMacName}"
            : "「auto」でMacを自動検出、またはIPアドレスを入力";
    }

    private void UpdateStats()
    {
        if (_app.Orchestrator?.AudioService is { } audio && audio.IsStreaming)
        {
            AudioStatsText.Text = $"パケット: {audio.PacketsSent:#,0} · 送信: {FormatBytes(audio.BytesSent)}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing
        e.Cancel = true;
        Hide();
    }
}
