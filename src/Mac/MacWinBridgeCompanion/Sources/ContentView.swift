// Mac-Win Bridge Companion: Main content view (SwiftUI).

import SwiftUI

struct ContentView: View {
    @ObservedObject var service: BridgeService
    
    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                
                // ── 接続ステータス ──
                connectionCard
                
                // ── ディスプレイモード ──
                displayCard
                
                // ── 映像配信 (Macモード時) ──
                if service.displayMode == .mac {
                    videoStreamCard
                }
                
                // ── 音声 ──
                audioCard
                
                // ── Smart KVM ──
                kvmCard
                
            }
            .padding(20)
        }
        .frame(minWidth: 320, idealWidth: 360, minHeight: 400)
        .background(Color(nsColor: .windowBackgroundColor))
    }
    
    // MARK: - Connection Card
    
    private var connectionCard: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                // Status indicator
                Circle()
                    .fill(service.isConnected ? Color.green : Color.red)
                    .frame(width: 12, height: 12)
                    .overlay(
                        Circle()
                            .stroke(service.isConnected ? Color.green.opacity(0.4) : Color.clear, lineWidth: 3)
                            .frame(width: 20, height: 20)
                    )
                
                VStack(alignment: .leading, spacing: 2) {
                    Text(service.isConnected ? "接続済み" : "未接続")
                        .font(.headline)
                        .foregroundColor(.primary)
                    
                    Text(service.statusMessage)
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .lineLimit(1)
                }
                
                Spacer()
                
                if service.isConnected {
                    Button("切断") {
                        service.disconnect()
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    .controlSize(.small)
                }
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }
    
    // MARK: - Display Card
    
    private var displayCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "display.2")
                    .foregroundColor(.blue)
                Text("ディスプレイモード")
                    .font(.subheadline.weight(.semibold))
                
                Spacer()
                
                Text(service.displayMode == .mac ? "Mac 配信中" : "Windows")
                    .font(.caption)
                    .foregroundColor(service.displayMode == .mac ? .green : .secondary)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(
                        Capsule()
                            .fill(service.displayMode == .mac
                                  ? Color.green.opacity(0.15)
                                  : Color.secondary.opacity(0.1))
                    )
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }
    
    // MARK: - Video Stream Card (Mac mode)
    
    private var videoStreamCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "video.fill")
                    .foregroundColor(.orange)
                Text("映像配信")
                    .font(.subheadline.weight(.semibold))
                
                Spacer()
                
                if service.screenStreamer.isStreaming {
                    Circle()
                        .fill(Color.red)
                        .frame(width: 8, height: 8)
                    Text("LIVE")
                        .font(.caption2.weight(.bold))
                        .foregroundColor(.red)
                }
            }
            
            // Stats grid
            HStack(spacing: 16) {
                statItem(title: "FPS", value: String(format: "%.1f", service.screenStreamer.fps))
                Divider().frame(height: 30)
                statItem(title: "送信", value: formatBytes(service.screenStreamer.encodedBytesPerSec) + "/s")
                Divider().frame(height: 30)
                statItem(title: "フレーム", value: "\(service.videoFramesSent)")
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }
    
    // MARK: - Audio Card
    
    private var audioCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "speaker.wave.2.fill")
                    .foregroundColor(.purple)
                Text("音声ストリーミング")
                    .font(.subheadline.weight(.semibold))
                
                Spacer()
                
                Text(service.audioStreaming ? "受信中" : "停止")
                    .font(.caption)
                    .foregroundColor(service.audioStreaming ? .green : .secondary)
            }
            
            if service.audioStreaming {
                HStack {
                    Text("受信パケット:")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Text("\(service.audioPacketsReceived)")
                        .font(.caption.monospacedDigit())
                }
            }
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }
    
    // MARK: - KVM Card
    
    private var kvmCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "keyboard.fill")
                    .foregroundColor(.teal)
                Text("Smart KVM")
                    .font(.subheadline.weight(.semibold))
                
                Spacer()
                
                // Focus indicator
                HStack(spacing: 4) {
                    Circle()
                        .fill(service.isFocusOnMac ? Color.green : Color.gray)
                        .frame(width: 8, height: 8)
                    Text(service.isFocusOnMac ? "フォーカス: Mac" : "フォーカス: Windows")
                        .font(.caption)
                        .foregroundColor(service.isFocusOnMac ? .green : .secondary)
                }
            }
            
            Text("画面端でWindowsのマウス/キーボードがMacに自動転送されます")
                .font(.caption2)
                .foregroundColor(.secondary)
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(10)
    }
    
    // MARK: - Helpers
    
    private func statItem(title: String, value: String) -> some View {
        VStack(spacing: 2) {
            Text(title)
                .font(.caption2)
                .foregroundColor(.secondary)
            Text(value)
                .font(.system(.callout, design: .monospaced).weight(.semibold))
                .foregroundColor(.primary)
        }
    }
    
    private func formatBytes(_ bytes: Int) -> String {
        if bytes >= 1_000_000 {
            return String(format: "%.1f MB", Double(bytes) / 1_000_000)
        } else if bytes >= 1000 {
            return String(format: "%.0f KB", Double(bytes) / 1000)
        }
        return "\(bytes) B"
    }
}
