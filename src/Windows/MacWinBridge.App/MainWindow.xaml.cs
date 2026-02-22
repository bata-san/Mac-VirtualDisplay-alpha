using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Display.Monitor;

namespace MacWinBridge.App;

public partial class MainWindow : Window
{
    private readonly App _app;

    // ステータスドット用カラー
    private static readonly SolidColorBrush ConnectedColor =
        new(Color.FromRgb(0x16, 0xA3, 0x4A));   // #16A34A
    private static readonly SolidColorBrush DisconnectedColor =
        new(Color.FromRgb(0xDC, 0x26, 0x26));   // #DC2626
    private static readonly SolidColorBrush BadgeConnected =
        new(Color.FromRgb(0x06, 0x2B, 0x15));   // 濃い緑背景
    private static readonly SolidColorBrush BadgeDisconnected =
        new(Color.FromRgb(0x1A, 0x1A, 0x2E));   // 暗い紫背景

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        MacHostInput.Text = _app.Config.MacHost;
        UpdateDisplayModeUI(_app.Config.Display.Mode);
        UpdateAudioRoutingUI(_app.Config.Audio.Routing);
        UpdateFooter();

        // Wire up orchestrator events
        if (_app.Orchestrator is not null)
        {
            _app.Orchestrator.ConnectionChanged += (_, connected) =>
                Dispatcher.Invoke(() => OnConnectionChanged(connected));
        }
    }

    private void UpdateFooter()
    {
        try
        {
            var monitors = MonitorManager.GetMonitors();
            FooterText.Text = $"Mac-Win Bridge v0.1.0  ·  モニター {monitors.Count}台";
        }
        catch
        {
            FooterText.Text = "Mac-Win Bridge v0.1.0";
        }
    }

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
        // OnConnectionChanged が呼ばれてボタンテキストを更新
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

    // ── Audio Routing ────────────────────────────────
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
        UpdateAudioRoutingUI(routing);
    }

    private void UpdateAudioRoutingUI(AudioRouting routing)
    {
        var primary = (SolidColorBrush)FindResource("PrimaryBrush");
        var active = (SolidColorBrush)FindResource("SuccessBrush");

        AudioToMacButton.Background = routing == AudioRouting.WindowsToMac ? active : primary;
        AudioToWinButton.Background = routing == AudioRouting.MacToWindows ? active : primary;
        AudioToBothButton.Background = routing == AudioRouting.Both ? active : primary;

        AudioRoutingText.Text = routing switch
        {
            AudioRouting.WindowsToMac => "Win → Mac",
            AudioRouting.MacToWindows => "Mac → Win",
            AudioRouting.Both => "両方",
            _ => "不明",
        };
    }

    private void UpdateDisplayModeUI(DisplayMode mode)
    {
        var primary = (SolidColorBrush)FindResource("PrimaryBrush");
        var active = (SolidColorBrush)FindResource("SuccessBrush");

        WindowsModeButton.Background = mode == DisplayMode.Windows ? active : primary;
        MacModeButton.Background = mode == DisplayMode.Mac ? active : primary;

        DisplayModeText.Text = mode switch
        {
            DisplayMode.Windows => "Windowsモード",
            DisplayMode.Mac => "Macモード",
            _ => "不明",
        };
    }

    private void OnConnectionChanged(bool connected)
    {
        // ── バッジ全体の背景 ──
        StatusBadge.Background = connected ? BadgeConnected : BadgeDisconnected;

        // ── インジケータードット ──
        StatusDot.Fill = connected ? ConnectedColor : DisconnectedColor;
        StatusRing.Stroke = connected ? ConnectedColor : DisconnectedColor;
        StatusRing.Opacity = connected ? 0.45 : 0.0;

        // ── テキスト ──
        ConnectionLabel.Text = connected ? "接続済み" : "未接続";
        ConnectionSubLabel.Text = connected
            ? $"Mac に接続中 — {_app.Orchestrator?.ConnectedMacName}"
            : "Macが見つかりません";

        // ── ボタン ──
        ConnectButton.Content = connected ? "切断" : "接続";

        // ── KVM ステータス ──
        KvmStatusText.Text = connected ? "アクティブ" : "停止";
        KvmStatusText.Foreground = connected ? ConnectedColor : DisconnectedColor;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // トレイに最小化
        e.Cancel = true;
        Hide();
    }
}
