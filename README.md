# 🌉 Mac-Win Bridge

**WindowsのシステムオーディオをMac経由のヘッドホン1つで完結させる統合オーディオブリッジ**

---

## ✨ 主要機能

### 🔊 Unified Audio（統合オーディオ）

Windows側の全音声をMac経由のヘッドホン1つで完結させます。

```
Windows (WASAPI Loopback) → PCM変換 → TCP送信 → Mac (AVAudioEngine) でミックス再生
```

- WASAPI Loopbackでシステム音声を完全キャプチャ
- 32bit Float → 16bit PCM 変換（48kHz/2ch）
- Mac側でmacOS音声とリアルタイムミックス

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
│  │ .Audio           │──│  - Config        │             │
│  │ - WASAPI Capture │  │  - Discovery     │             │
│  │ - PCM Convert    │  └──────────────────┘             │
│  └──────────────────┘          │                        │
└──────────────────────────────┬──────────────────────────┘
                               │ TCP 42102 / USB-C / LAN
┌──────────────────────────────┴──────────────────────────┐
│  Mac                                                     │
│  ┌──────────────────────────────────────────────┐        │
│  │  MacWinBridgeCompanion (Swift/SwiftUI)       │        │
│  │  ┌────────────────────────────────────────┐  │        │
│  │  │ Audio Mixer (AVAudioEngine)            │  │        │
│  │  │  - Windows音声受信                     │  │        │
│  │  │  - macOS音声とリアルタイムミックス      │  │        │
│  │  └────────────────────────────────────────┘  │        │
│  └──────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────┘
```

---

## 📁 プロジェクト構成

```
MacToWinTool/
├── MacWinBridge.sln
├── src/
│   ├── Windows/
│   │   ├── MacWinBridge.Core/          # 共通プロトコル、設定、トランスポート
│   │   │   ├── Protocol/
│   │   │   ├── Transport/
│   │   │   ├── Configuration/
│   │   │   └── Discovery/
│   │   ├── MacWinBridge.Audio/         # 統合オーディオモジュール
│   │   │   ├── Capture/               # WASAPI Loopback キャプチャ
│   │   │   └── Processing/            # PCMフォーマット変換
│   │   └── MacWinBridge.App/           # WPFメインアプリケーション
│   └── Mac/
│       └── MacWinBridgeCompanion/      # macOSコンパニオンアプリ (Swift)
├── protocol/
│   └── bridge_protocol.md
└── docs/
    └── architecture.md
```

---

## 🔌 通信プロトコル

| ポート | 用途 | 内容 |
|--------|------|------|
| `42100` | Control | ハンドシェイク、ハートビート |
| `42102` | Audio | 音声PCMストリーム |

各メッセージは8バイトヘッダー `[Type:2][Flags:2][Length:4]` + ペイロード。

---

## 🚀 ビルド & 実行

### Windows側

```bash
dotnet restore MacWinBridge.sln
dotnet build MacWinBridge.sln -c Release
dotnet run --project src/Windows/MacWinBridge.App
```

### Mac側

```bash
cd src/Mac/MacWinBridgeCompanion
swift build
swift run
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
  "AudioPort": 42102,
  "Audio": {
    "Enabled": true,
    "SampleRate": 48000,
    "Channels": 2,
    "BitsPerSample": 16,
    "BufferMs": 10
  }
}
```

---

## 📋 動作環境

| 項目 | Windows | Mac |
|------|---------|-----|
| OS | Windows 10/11 | macOS 14 Sonoma+ |
| ランタイム | .NET 6 | Swift 5.9 |
| 接続 | USB-C / LAN / Wi-Fi | USB-C / LAN / Wi-Fi |

---

## 🗺️ ロードマップ

- [ ] Opus音声圧縮
- [ ] 自動再接続
- [ ] 暗号化通信（TLS）

---

## 📄 ライセンス

MIT License
