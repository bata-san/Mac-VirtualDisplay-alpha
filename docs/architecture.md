# Mac-Win Bridge: 設計ドキュメント

## 設計思想

### 低レイテンシー最優先
- 映像: DXGI Desktop Duplication（カーネルレベルキャプチャ） + デルタ圧縮
- 音声: WASAPI Loopback（10msバッファ） + 最小限のフォーマット変換
- 入力: Priority フラグ付きメッセージで音声・映像フレームに割り込み

### モジュラー設計
各機能（映像・音声・入力）は独立したプロジェクトとして実装され、単独でも使用可能。

### セーフティ
- デッドゾーン: マウスが画面端に到達してもすぐには切り替わらない
- ホットキー: `Ctrl+Alt+K` で強制的にWindows操作に復帰
- 自動復帰: 切断時は自動的にWindows入力に戻る

## Windows側モジュール詳細

### MacWinBridge.Core
- **BridgeProtocol**: 8バイトヘッダー + ペイロードのバイナリフレーミング
- **BridgeTransport**: `System.IO.Pipelines` ベースの高性能TCP通信
- **BridgeConfig**: `System.Text.Json` による設定管理 (`%APPDATA%\MacWinBridge\config.json`)
- **BridgeDiscovery**: UDPブロードキャストによるMac自動検出

### MacWinBridge.Display
- **DesktopDuplicationCapture**: Vortice.Windows で DXGI Output Duplication API を使用
  - ステージングテクスチャでGPU→CPU転送
  - BGRA ピクセルフォーマット
- **MonitorManager**: Win32 `EnumDisplayMonitors` によるモニター列挙
- **VideoStreamer**: キャプチャ→デルタエンコード→TCP送信パイプライン
- **DisplaySwitchService**: Windows/Macモード切替のオーケストレーション

### MacWinBridge.Audio
- **WasapiAudioCapture**: NAudio の `WasapiLoopbackCapture` でシステム音声キャプチャ
  - ループバック: 特別なドライバなしで全音声を取得
- **AudioFormatConverter**: 32-bit Float → 16-bit PCM 変換 + リサンプリング
- **AudioStreamService**: キャプチャ→変換→TCP送信パイプライン

### MacWinBridge.Input
- **GlobalInputHook**: `SetWindowsHookEx` WH_MOUSE_LL / WH_KEYBOARD_LL
  - `IntPtr(1)` 返却でイベントを抑制（Macモード時）
- **SmartKvmService**: 画面端検出 + カーソルクリップ + 入力転送ロジック

### MacWinBridge.App
- **BridgeOrchestrator**: 全サービスのライフサイクル管理
- **MainWindow** (WPF): ダークテーマUI、カード型レイアウト
- システムトレイ常駐（Hardcodet.NotifyIcon.Wpf）

## Mac側モジュール詳細

### BridgeService
- NWListener で3ポートをリスニング
- メッセージルーティング: ヘッダーのType値で各ハンドラーにディスパッチ

### VideoReceiver
- CGContext/CGImage でBGRAピクセルデータからフレーム描画
- NSWindow (borderless, fullscreen) で全画面表示

### AudioMixer
- AVAudioEngine + AVAudioPlayerNode
- PCMバッファをスケジュール → mainMixerNode でmacOS音声とミックス

### InputInjector
- CGEvent API でマウス/キーボードイベントを注入
- Windows VK → macOS キーコード変換テーブル
- アクセシビリティ権限が必要（Security & Privacy → Accessibility）

## パフォーマンス考慮

### 映像帯域
- Raw BGRA 1920x1080@60fps ≈ 29.5 GB/s → **デルタ圧縮で90%以上削減**
- 将来的なH.264エンコードで 5-30 Mbps に抑制

### 音声帯域
- PCM 48kHz/16bit/2ch = 1.5 Mbps → **Opus (将来対応) で128 kbps**

### USB-C ネットワーク
- RNDIS/CDC-ECM は通常 100Mbps-1Gbps の仮想NIC
- Raw BGRAでも画面非変化時はほぼゼロ帯域
