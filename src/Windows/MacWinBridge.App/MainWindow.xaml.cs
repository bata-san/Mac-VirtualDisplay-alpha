using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Display.Monitor;

namespace MacWinBridge.App;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly StringBuilder _logBuffer = new();
    private bool _logPanelOpen = false;
    private const int MaxDisplayLines = 300;
    private int _displayLineCount = 0;

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
        PopulateMonitorList();

        // USB スキャンはバックグラウンドで（WMI は遅い）
        _ = Task.Run(UsbPortScanner.GetPorts)
            .ContinueWith(t => Dispatcher.Invoke(() => PopulateUsbInfo(t.Result)),
                TaskContinuationOptions.OnlyOnRanToCompletion);

        // ── ログ購読 ──
        // 既存バッファを表示
        foreach (var line in AppLogger.GetBufferedLines())
            AppendLogLine(line);

        AppLogger.LineAdded += line =>
            Dispatcher.BeginInvoke(() => AppendLogLine(line));

        // Wire up orchestrator events
        if (_app.Orchestrator is not null)
        {
            _app.Orchestrator.ConnectionChanged += (_, connected) =>
                Dispatcher.Invoke(() => OnConnectionChanged(connected));
        }
    }

    // ── ログパネル操作 ───────────────────────────
    private void AppendLogLine(string line)
    {
        _logBuffer.AppendLine(line);
        _displayLineCount++;

        // バッファが上限を超えた場合は上から削る
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

        // 最下部に自動スクロール
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

    // ── モニター選択 ────────────────────────────────────────────────────────
    private void PopulateMonitorList()
    {
        MonitorListPanel.Children.Clear();
        var monitors = MonitorManager.GetMonitors();
        var target = _app.Config.Display.TargetMonitorIndex;

        foreach (var m in monitors)
        {
            var isTarget = m.Index == target;
            var primary  = (SolidColorBrush)FindResource("PrimaryBrush");
            var success  = (SolidColorBrush)FindResource("SuccessBrush");
            var textBrush = (SolidColorBrush)FindResource("TextBrush");
            var mutedBrush = (SolidColorBrush)FindResource("TextMutedBrush");

            var row = new Grid { Margin = new Thickness(0, 0, 0, m.Index < monitors.Count - 1 ? 8 : 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel();
            infoStack.Children.Add(new TextBlock
            {
                Text       = (m.IsPrimary ? "🖥️ " : "🖥️ ") + $"モニター {m.Index + 1}" +
                             (m.IsPrimary ? "  (メイン)" : ""),
                FontSize   = 12,
                FontWeight = isTarget ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isTarget ? textBrush : mutedBrush,
            });
            infoStack.Children.Add(new TextBlock
            {
                Text       = $"{m.Width}×{m.Height}  {m.RefreshRate} Hz",
                FontSize   = 11,
                Foreground = mutedBrush,
            });

            var btn = new Button
            {
                Content    = isTarget ? "✓ 選択中" : "選択",
                Background = isTarget ? success : primary,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding    = new Thickness(12, 5, 12, 5),
                FontSize   = 11,
                Cursor     = System.Windows.Input.Cursors.Hand,
                Tag        = m.Index,
                Style      = null,  // デフォルトスタイルをリセット
            };
            btn.Style   = (Style)FindResource("BridgeButton");
            btn.Content = isTarget ? "✓ 選択中" : "選択";
            btn.Background = isTarget ? success : primary;
            btn.Tag     = m.Index;
            btn.Click  += OnSelectMonitorClick;

            Grid.SetColumn(infoStack, 0);
            Grid.SetColumn(btn, 1);
            row.Children.Add(infoStack);
            row.Children.Add(btn);

            MonitorListPanel.Children.Add(row);
        }
    }

    private void OnSelectMonitorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        _app.Config.Display.TargetMonitorIndex = idx;
        _app.Config.Save();
        AppLogger.Info($"切替対象モニターを {idx + 1} に変更");
        PopulateMonitorList();
    }

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) =>
        PopulateMonitorList();

    // ── USB-C / Thunderbolt 情報 ──────────────────────────────────────────
    private void PopulateUsbInfo(List<UsbPortInfo> ports)
    {
        UsbInfoPanel.Children.Clear();

        if (ports.Count == 0)
        {
            UsbInfoPanel.Children.Add(new TextBlock
            {
                Text       = "USB-C / Thunderbolt ポートが見つかりませんでした",
                FontSize   = 11,
                Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
            });
            return;
        }

        var mutedBrush = (SolidColorBrush)FindResource("TextMutedBrush");
        var textBrush  = (SolidColorBrush)FindResource("TextBrush");

        foreach (var port in ports)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBlock = new TextBlock
            {
                Text       = port.Icon,
                FontSize   = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 8, 0),
            };

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text       = port.Name,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text       = port.SpeedLabel,
                FontSize   = 11,
                Foreground = GetSpeedBrush(port.Speed),
            });

            Grid.SetColumn(iconBlock, 0);
            Grid.SetColumn(info, 1);
            row.Children.Add(iconBlock);
            row.Children.Add(info);
            UsbInfoPanel.Children.Add(row);
        }
    }

    private static Brush GetSpeedBrush(UsbSpeed speed) => speed switch
    {
        UsbSpeed.Thunderbolt5 or UsbSpeed.Thunderbolt4 or UsbSpeed.Thunderbolt3
            => new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),  // 金色
        UsbSpeed.Usb4Gen3 or UsbSpeed.Usb4Gen2
            => new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),  // 水色
        UsbSpeed.Usb3Gen2x2 or UsbSpeed.Usb3Gen2
            => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),  // 緑
        UsbSpeed.Usb3Gen1
            => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),  // グレー
        _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
    };

    private void OnRefreshUsbClick(object sender, RoutedEventArgs e)
    {
        UsbInfoPanel.Children.Clear();
        UsbInfoPanel.Children.Add(new TextBlock
        {
            Text       = "スキャン中...",
            FontSize   = 11,
            Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
        });
        _ = Task.Run(UsbPortScanner.GetPorts)
            .ContinueWith(t => Dispatcher.Invoke(() => PopulateUsbInfo(t.Result)),
                TaskContinuationOptions.OnlyOnRanToCompletion);
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

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.LineAdded -= line => Dispatcher.BeginInvoke(() => AppendLogLine(line));
        base.OnClosed(e);
    }
}
