# Mac-Win Bridge: ワイヤプロトコル仕様 v0.1

## 概要

Mac-Win Bridgeは、Windows PCとMac間でリアルタイムの映像・音声・入力イベントを転送するための独自バイナリプロトコルを使用します。

## トランスポート

- **プロトコル**: TCP (信頼性のある順序付き配信)
- **接続方式**: Windows→Mac (Windowsがクライアント、Macがサーバー)
- **バイトオーダー**: Little-Endian
- **自動検出**: UDP ブロードキャスト（ポート42099）

### ポート割り当て

| ポート | チャネル | 用途 |
|--------|----------|------|
| 42099 | Discovery | UDP自動検出 |
| 42100 | Control | ハンドシェイク、ハートビート、入力イベント |
| 42101 | Video | 映像フレーム |
| 42102 | Audio | 音声PCMデータ |

## メッセージフォーマット

### ヘッダー (8 bytes)

```
Offset  Size  Field         Description
0       2     Type          メッセージ種別 (uint16)
2       2     Flags         フラグビットフィールド (uint16)
4       4     PayloadLen    ペイロード長 (uint32)
```

### フラグ

| Bit | 名前 | 説明 |
|-----|------|------|
| 0 | Compressed | ペイロードが圧縮されている |
| 1 | Encrypted | ペイロードが暗号化されている |
| 2 | Priority | 優先メッセージ（入力イベント等） |
| 3 | KeyFrame | 映像キーフレーム |

## メッセージ種別

### 接続管理 (0x00xx)

#### Handshake (0x0001)
接続時にWindowsから送信。JSON形式ペイロード。

```json
{
    "AppVersion": "0.1.0",
    "DeviceName": "DESKTOP-ABC",
    "Platform": "Windows",
    "DisplayWidth": 2560,
    "DisplayHeight": 1440,
    "RefreshRate": 60,
    "SupportsAudio": true,
    "SupportsInput": true
}
```

#### HandshakeAck (0x0002)
Macからの応答。同形式のJSON。

#### Heartbeat (0x0003)
30秒間隔。ペイロードなし。

#### Disconnect (0x0004)
正常切断通知。ペイロードなし。

### 映像 (0x01xx)

#### VideoConfig (0x0101)
映像パラメータ通知。JSON形式。

```json
{
    "Width": 2560,
    "Height": 1440,
    "PixelFormat": "BGRA",
    "Codec": "raw"
}
```

#### VideoFrame (0x0100)
映像フレームデータ。

```
Offset  Size  Field        Description
0       4     Width        フレーム幅 (int32)
4       4     Height       フレーム高さ (int32)
8       4     Stride       行ストライド (int32)
12      4     FrameNumber  フレーム番号 (int32)
16      N     PixelData    ピクセルデータ (BGRA or delta)
```

- **KeyFrame フラグ**: 完全なBGRAフレーム
- **Compressed フラグ**: 前フレームとのXORデルタ

#### DisplaySwitch (0x0102)
ディスプレイモード切替通知。JSON形式。

```json
{
    "Mode": "Mac",
    "Timestamp": 1708646400000
}
```

### 音声 (0x02xx)

#### AudioConfig (0x0201)
音声パラメータ通知。JSON形式。

```json
{
    "SampleRate": 48000,
    "Channels": 2,
    "BitsPerSample": 16,
    "BufferMs": 10,
    "Compressed": false
}
```

#### AudioData (0x0200)
PCM音声データ。

```
Offset  Size  Field        Description
0       8     Timestamp    タイムスタンプ (int64, ticks)
8       N     PcmData      PCM 16-bit サンプルデータ
```

### 入力イベント (0x03xx)

#### MouseMove (0x0300)
```
Offset  Size  Field  Description
0       4     X      X座標 (int32)
4       4     Y      Y座標 (int32)
```

#### MouseButton (0x0301)
```
Offset  Size  Field   Description
0       4     Action  1=LeftDown, 2=LeftUp, 3=RightDown, 4=RightUp, 5=MiddleDown, 6=MiddleUp
```

#### MouseScroll (0x0302)
```
Offset  Size  Field        Description
0       4     IsHorizontal 0=垂直, 1=水平
4       4     Delta        スクロール量 (WHEEL_DELTA単位)
```

#### KeyDown (0x0310) / KeyUp (0x0311)
```
Offset  Size  Field       Description
0       4     VkCode      Windows仮想キーコード (int32)
4       4     ScanCode    スキャンコード (int32)
8       4     IsExtended  拡張キーフラグ (int32, 0 or 1)
```

## 接続シーケンス

```
Windows                          Mac
   │                              │
   │──── [Discovery UDP] ────────>│ (ポート42099)
   │<─── [Discovery Response] ────│
   │                              │
   │──── [TCP Connect :42100] ───>│ Control
   │──── [TCP Connect :42101] ───>│ Video
   │──── [TCP Connect :42102] ───>│ Audio
   │                              │
   │──── [Handshake] ───────────>│
   │<─── [HandshakeAck] ─────────│
   │                              │
   │──── [AudioConfig] ─────────>│
   │──── [AudioData ...] ───────>│ (continuous)
   │                              │
   │──── [Heartbeat] ──────────->│ (every 30s)
   │<─── [Heartbeat] ────────────│
   │                              │
   │  (user switches to Mac mode) │
   │──── [DisplaySwitch] ───────>│
   │──── [VideoConfig] ─────────>│
   │──── [VideoFrame ...] ──────>│ (continuous)
   │                              │
   │  (mouse crosses edge)        │
   │──── [MouseMove ...] ───────>│ (continuous)
   │──── [KeyDown/KeyUp] ───────>│
   │                              │
```
