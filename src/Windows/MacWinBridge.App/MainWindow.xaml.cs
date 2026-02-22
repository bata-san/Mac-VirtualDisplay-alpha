using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacWinBridge.Audio;
using MacWinBridge.Core.Configuration;

namespace MacWinBridge.App;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly StringBuilder _logBuffer = new();
    private bool _logPanelOpen = false;
    private const int MaxDisplayLines = 300;
    private int _displayLineCount = 0;

    private static readonly SolidColorBrush ConnectedColor =
        new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush DisconnectedColor =
        new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush BadgeConnected =
        new(Color.FromRgb(0x06, 0x2B, 0x15));
    private static readonly SolidColorBrush BadgeDisconnected =
        new(Color.FromRgb(0x1A, 0x1A, 0x2E));

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        MacHostInput.Text = _app.Config.MacHost;
        UpdateAudioRoutingUI(_app.Config.Audio.Routing);
        UpdateFooter();

        foreach (var line in AppLogger.GetBufferedLines())
            AppendLogLine(line);
        AppLogger.LineAdded += OnLogLineAdded;

        if (_app.Orchestrator is not null)
        {
            _app.Orchestrator.ConnectionChanged += (_, connected) =>
                Dispatcher.Invoke(() => OnConnectionChanged(connected));
        }
    }

    private void OnLogLineAdded(string line) =>
        Dispatcher.BeginInvoke(() => AppendLogLine(line));

    // ── ログパネル ───────────────────────────────────────────────────────
    private void AppendLogLine(string line)
    {
        _logBuffer.AppendLine(line);
        _displayLineCount++;

        if (_displayLineCount > MaxDisplayLines)
        {
            var text = _logBuffer.ToString();
            var idx = text.IndexOf('\n', text.Length / 3);
            if (idx >= 0)
            {
                _logBuffer.Clear();
                _logBuffer.Append(text[(idx + 1)..]);
                _displayLineCount = _logBuffer.ToString().Split('\n').Length;
            }
        }

        LogText.Text = _logBuffer.ToString();
        LogScroller.ScrollToEnd();
    }

    private void OnLogToggle(object sender, RoutedEventArgs e)
    {
        _logPanelOpen = !_logPanelOpen;
        LogPanel.Visibility = _logPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        LogToggleButton.Content = _logPanelOpen ? "📋 隠す" : "📋 ログ";
        Height = _logPanelOpen ? 740 : 540;
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppLogger.LogFilePath;
            if (!string.IsNullOrEmpty(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _displayLineCount = 0;
        LogText.Text = string.Empty;
        AppLogger.Info("ログをクリアしました");
    }

    private void UpdateFooter()
    {
        FooterText.Text = "Mac-Win Bridge v0.1.0";
    }

    // ── 接続 ─────────────────────────────────────────────────────────────
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_app.Orchestrator is null) return;

        if (_app.Orchestrator.IsConnected)
        {
            await _app.Orchestrator.DisconnectAsync();
            return;
        }

        _app.Config.MacHost = MacHostInput.Text.Trim();
        _app.Config.Save();

        ConnectButton.Content = "接続中...";
        ConnectButton.IsEnabled = false;

        await _app.Orchestrator.ConnectAsync();
        ConnectButton.IsEnabled = true;
    }

    // ── Audio Routing ─────────────────────────────────────────────────────
    private async void OnAudioToMacClick(object sender, RoutedEventArgs e) =>
        await SetAudioRoutingAsync(AudioRouting.WindowsToMac);

    private async void OnAudioToWinClick(object sender, RoutedEventArgs e) =>
        await SetAudioRoutingAsync(AudioRouting.MacToWindows);

    private async void OnAudioToBothClick(object sender, RoutedEventArgs e) =>
        await SetAudioRoutingAsync(AudioRouting.Both);

    private async Task SetAudioRoutingAsync(AudioRouting routing)
    {
        if (_app.Orchestrator?.AudioService is null) return;
        await _app.Orchestrator.AudioService.SetRoutingAsync(routing);
        _app.Config.Audio.Routing = routing;
        _app.Config.Save();
        UpdateAudioRoutingUI(routing);
    }

    private void UpdateAudioRoutingUI(AudioRouting routing)
    {
        var primary = (SolidColorBrush)FindResource("PrimaryBrush");
        var active  = (SolidColorBrush)FindResource("SuccessBrush");

        AudioToMacButton.Background  = routing == AudioRouting.WindowsToMac ? active : primary;
        AudioToWinButton.Background  = routing == AudioRouting.MacToWindows  ? active : primary;
        AudioToBothButton.Background = routing == AudioRouting.Both           ? active : primary;

        AudioRoutingText.Text = routing switch
        {
            AudioRouting.WindowsToMac => "Win → Mac",
            AudioRouting.MacToWindows => "Mac → Win",
            AudioRouting.Both         => "両方",
            _                         => "不明",
        };
    }

    // ── 接続状態変化 ─────────────────────────────────────────────────────
    private void OnConnectionChanged(bool connected)
    {
        StatusBadge.Background = connected ? BadgeConnected : BadgeDisconnected;
        StatusDot.Fill         = connected ? ConnectedColor  : DisconnectedColor;
        StatusRing.Stroke      = connected ? ConnectedColor  : DisconnectedColor;
        StatusRing.Opacity     = connected ? 0.45 : 0.0;

        ConnectionLabel.Text    = connected ? "接続済み" : "未接続";
        ConnectionSubLabel.Text = connected
            ? $"Mac に接続中 — {_app.Orchestrator?.ConnectedMacName}"
            : "Macが見つかりません";

        ConnectButton.Content = connected ? "切断" : "接続";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.LineAdded -= OnLogLineAdded;
        base.OnClosed(e);
    }
}
