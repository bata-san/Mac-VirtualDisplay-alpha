// Mac-Win Bridge Companion: Main content view.

import SwiftUI

struct ContentView: View {
    @EnvironmentObject var bridge: BridgeService
    
    var body: some View {
        VStack(spacing: 0) {
            // Header
            HStack {
                Text("🌉 Mac-Win Bridge")
                    .font(.title)
                    .fontWeight(.bold)
                Spacer()
                Circle()
                    .fill(bridge.isConnected ? Color.green : Color.red)
                    .frame(width: 12, height: 12)
            }
            .padding(.bottom, 4)
            
            Text(bridge.statusMessage)
                .font(.caption)
                .foregroundColor(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 20)
            
            // Connection Card
            CardView(title: "📡 接続状態") {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(bridge.isConnected ? "接続済み" : "待機中",
                              systemImage: bridge.isConnected ? "checkmark.circle.fill" : "clock")
                            .foregroundColor(bridge.isConnected ? .green : .orange)
                        
                        if let host = bridge.connectedHost {
                            Text("Windows: \(host)")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    }
                    Spacer()
                    
                    if bridge.isConnected {
                        Button("切断") {
                            bridge.disconnect()
                        }
                        .buttonStyle(.bordered)
                        .tint(.red)
                    }
                }
            }
            
            // Display Mode Card
            CardView(title: "🖥️ ディスプレイモード") {
                HStack(spacing: 12) {
                    ModeButton(
                        label: "🪟 Windows",
                        isActive: bridge.displayMode == .windows
                    )
                    
                    ModeButton(
                        label: "🍎 Mac",
                        isActive: bridge.displayMode == .mac
                    )
                }
                
                Text("現在: \(bridge.displayMode == .mac ? "Macモード" : "Windowsモード")")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .padding(.top, 4)
            }
            
            // Audio Card
            CardView(title: "🔊 統合オーディオ") {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(bridge.audioStreaming ? "受信中" : "停止",
                              systemImage: bridge.audioStreaming ? "speaker.wave.3.fill" : "speaker.slash")
                            .foregroundColor(bridge.audioStreaming ? .green : .secondary)
                        
                        Text("Windows音声をMacでミックス再生")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                    Spacer()
                    
                    Text("\(bridge.audioPacketsReceived) パケット")
                        .font(.caption2)
                        .foregroundColor(.secondary)
                }
            }
            
            // KVM Card
            CardView(title: "⌨️ Smart KVM") {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(bridge.isConnected ? "待機中" : "未接続",
                              systemImage: "keyboard")
                            .foregroundColor(bridge.isConnected ? .blue : .secondary)
                        
                        Text("Windowsマウスが画面端に到達で自動切替")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                    Spacer()
                }
            }
            
            Spacer()
            
            // Footer
            Text("Mac-Win Bridge Companion v0.1.0")
                .font(.caption2)
                .foregroundColor(.secondary)
        }
        .padding(24)
        .frame(minWidth: 420, minHeight: 500)
    }
}

// MARK: - Subviews

struct CardView<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content
    
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.headline)
            
            content()
        }
        .padding(16)
        .background(Color(nsColor: .controlBackgroundColor))
        .cornerRadius(12)
        .padding(.bottom, 8)
    }
}

struct ModeButton: View {
    let label: String
    let isActive: Bool
    
    var body: some View {
        Text(label)
            .font(.system(size: 14, weight: .semibold))
            .frame(maxWidth: .infinity)
            .padding(.vertical, 10)
            .background(isActive ? Color.accentColor : Color(nsColor: .controlColor))
            .foregroundColor(isActive ? .white : .primary)
            .cornerRadius(8)
    }
}

struct MenuBarView: View {
    @EnvironmentObject var bridge: BridgeService
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Circle()
                    .fill(bridge.isConnected ? Color.green : Color.red)
                    .frame(width: 8, height: 8)
                Text(bridge.isConnected ? "接続済み" : "未接続")
                    .font(.caption)
            }
            
            Divider()
            
            if bridge.isConnected {
                Text("ディスプレイ: \(bridge.displayMode == .mac ? "Mac" : "Windows")")
                    .font(.caption)
                Text("オーディオ: \(bridge.audioStreaming ? "配信中" : "停止")")
                    .font(.caption)
                
                Divider()
                
                Button("切断") {
                    bridge.disconnect()
                }
            }
            
            Button("終了") {
                NSApplication.shared.terminate(nil)
            }
        }
        .padding(12)
        .frame(width: 200)
    }
}
