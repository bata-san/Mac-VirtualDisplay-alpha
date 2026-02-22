# 🌉 Mac-Win Bridge

**MacとWindowsをシームレスに統合する仮想KVM・映像・音声ブリッジ**

Windows PCに物理的に繋がっている2枚目モニターの表示をMac画面に切り替え、Windows音声をMac経由で出力し、マウス・キーボードを自動で切り替える統合ツールです。

---

## ✨ 主要機能

### 1. 🖥️ Display Switcher（映像切替）

2枚目モニターの表示内容をソフトウェアスイッチで切り替えます。

| モード | 動作 |
|--------|------|
| **Windowsモード** | 通常のデュアルスクリーン |
| **Macモード** | Macの画面をUSB-C/ネットワーク経由で受信し全画面表示 |

- DXGI Desktop Duplication API による低遅延キャプチャ
- デルタ圧縮 + 将来的なH.264/H.265エンコード対応
- 最大60FPS対応

### 2. 🔊 Unified Audio（統合オーディオ）

Windows側の全音声をMac経由のヘッドホン1つで完結させます。

```
Windows (WASAPI Loopback) → PCM変換 → TCP送信 → Mac (AVAudioEngine) でミックス再生
```

- WASAPI Loopbackでシステム音声を完全キャプチャ
- 32bit Float → 16bit PCM 変換（48kHz/2ch）
- Mac側でmacOS音声とリアルタイムミックス

### 3. ⌨️ Smart KVM（入力自動追従）

マウスが2枚目モニターの端に到達すると、自動的にMac操作に切り替わります。

- 低レベル `SetWindowsHookEx` によるグローバルフック
- Windows VK → macOS CGEvent 自動マッピング
- `Ctrl+Alt+K` で手動トグル
- デッドゾーン設定で誤操作防止

---

## 🏗️ アーキテクチャ

```
┌─────────────────────────────────────────────────────────┐
│  Windows PC                                             │
│  ┌──────────────────┐  ┌──────────────────┐             │
│  │ MacWinBridge.App │  │  MacWinBridge    │             │
│  │   (WPF UI)       │──│  .Core           │             │
│  └──────────────────┘  │  - Protocol      │             │
│  ┌──────────────────┐  │  - Transport     │             │
│  │ .Display         │──│  - Config        │             │
│  │ - DXGI Capture   │  │  - Discovery     │             │
│  │ - Video Stream   │  └──────────────────┘             │
│  └──────────────────┘          │                        │
│  ┌──────────────────┐          │ TCP (ports 42100-42102)│
│  │ .Audio           │          │                        │
│  │ - WASAPI Capture │──────────┤                        │
│  │ - PCM Convert    │          │                        │
│  └──────────────────┘          │                        │
│  ┌──────────────────┐          │                        │
│  │ .Input           │          │                        │
│  │ - Global Hooks   │──────────┘                        │
│  │ - Smart KVM      │                                   │
│  └──────────────────┘                                   │
└──────────────────────────────┬──────────────────────────┘
                               │ USB-C / LAN / Wi-Fi
┌──────────────────────────────┴──────────────────────────┐
│  Mac                                                     │
│  ┌──────────────────────────────────────────────┐        │
│  │  MacWinBridgeCompanion (Swift/SwiftUI)       │        │
│  │  ┌────────────┐ ┌────────────┐ ┌───────────┐│        │
│  │  │ Video      │ │ Audio      │ │ Input     ││        │
│  │  │ Receiver   │ │ Mixer      │ │ Injector  ││        │
│  │  │ (CGImage)  │ │(AVAudio    │ │ (CGEvent) ││        │
│  │  │            │ │ Engine)    │ │           ││        │
│  │  └────────────┘ └────────────┘ └───────────┘│        │
│  └──────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────┘
```

---

## 📁 プロジェクト構成

```
MacToWinTool/
├── MacWinBridge.sln                    # Visual Studio ソリューション
├── src/
│   ├── Windows/
│   │   ├── MacWinBridge.Core/          # 共通プロトコル、設定、トランスポート
│   │   │   ├── Protocol/              # バイナリワイヤプロトコル定義
│   │   │   ├── Transport/             # TCP通信 (System.IO.Pipelines)
│   │   │   ├── Configuration/         # JSON設定管理
│   │   │   └── Discovery/             # UDPブロードキャスト自動検出
│   │   ├── MacWinBridge.Display/       # 映像切替モジュール
│   │   │   ├── Capture/               # DXGI Desktop Duplication
│   │   │   ├── Monitor/               # モニター列挙・管理
│   │   │   └── Streaming/             # ビデオストリーミング
│   │   ├── MacWinBridge.Audio/         # 統合オーディオモジュール
│   │   │   ├── Capture/               # WASAPI Loopback キャプチャ
│   │   │   └── Processing/            # PCMフォーマット変換
│   │   ├── MacWinBridge.Input/         # Smart KVMモジュール
│   │   │   └── Hooks/                 # Win32グローバルフック
│   │   └── MacWinBridge.App/           # WPFメインアプリケーション
│   │       └── Services/              # オーケストレータ
│   └── Mac/
│       └── MacWinBridgeCompanion/      # macOSコンパニオンアプリ (Swift)
│           └── Sources/
├── protocol/
│   └── bridge_protocol.md             # プロトコル仕様書
└── docs/
    └── architecture.md                # 設計ドキュメント
```

---

## 🔌 通信プロトコル

3つのTCPポートを使用（USB-CネットワークまたはLAN経由）：

| ポート | 用途 | 内容 |
|--------|------|------|
| `42100` | Control | ハンドシェイク、ハートビート、KVM入力イベント |
| `42101` | Video | 映像フレームデータ |
| `42102` | Audio | 音声PCMストリーム |

各メッセージは8バイトヘッダー `[Type:2][Flags:2][Length:4]` + ペイロード。

---

## 🚀 ビルド & 実行

### Windows側

```bash
# .NET 8 SDK が必要
dotnet restore MacWinBridge.sln
dotnet build MacWinBridge.sln -c Release
dotnet run --project src/Windows/MacWinBridge.App
```

### Mac側

```bash
cd src/Mac/MacWinBridgeCompanion
swift build
swift run
# または Xcode で開く:
# open Package.swift
```

### 接続方法

1. **USB-C接続**: MacとWindowsをUSB-Cで接続（RNDIS/CDC-ECMネットワーク自動生成）
2. **Wi-Fi/LAN**: 同一ネットワーク上であれば自動検出（UDPブロードキャスト）
3. **手動設定**: Windows側アプリでMacのIPアドレスを直接指定

---

## ⚙️ 設定

設定ファイル: `%APPDATA%\MacWinBridge\config.json`

```json
{
  "MacHost": "auto",
  "ControlPort": 42100,
  "VideoPort": 42101,
  "AudioPort": 42102,
  "Display": {
    "TargetMonitorIndex": 1,
    "Mode": "Windows",
    "Codec": "H264",
    "BitrateMbps": 30,
    "MaxFps": 60
  },
  "Audio": {
    "Enabled": true,
    "SampleRate": 48000,
    "Channels": 2,
    "BitsPerSample": 16,
    "BufferMs": 10
  },
  "Input": {
    "Enabled": true,
    "TransitionEdge": "Right",
    "DeadZonePixels": 5,
    "ClipboardSync": true,
    "ToggleHotkey": "Ctrl+Alt+K"
  }
}
```

---

## 📋 動作環境

| 項目 | Windows | Mac |
|------|---------|-----|
| OS | Windows 10/11 | macOS 14 Sonoma+ |
| ランタイム | .NET 8 | Swift 5.9 |
| 接続 | USB-C / LAN / Wi-Fi | USB-C / LAN / Wi-Fi |
| 権限 | 管理者不要* | アクセシビリティ権限（入力注入） |

\* DXGI Desktop Duplication は管理者権限なしで動作しますが、セキュアデスクトップ(UAC)画面はキャプチャできません。

---

## 🗺️ ロードマップ

- [ ] H.264/H.265 ハードウェアエンコード（Media Foundation / FFmpeg）
- [ ] Opus音声圧縮
- [ ] クリップボード同期（テキスト・画像）
- [ ] ファイルドラッグ＆ドロップ転送
- [ ] マルチモニター対応（3台以上）
- [ ] 設定GUI（Mac側）
- [ ] 自動再接続
- [ ] 暗号化通信（TLS）

---

## 📄 ライセンス

MIT License
