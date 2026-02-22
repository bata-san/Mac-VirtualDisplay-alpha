// Mac-Win Bridge Companion: App entry point with menu bar + main window.

import SwiftUI

@main
struct MacWinBridgeCompanionApp: App {
    @StateObject private var bridgeService = BridgeService()
    
    var body: some Scene {
        // ── Main Window ──
        WindowGroup {
            ContentView(service: bridgeService)
                .navigationTitle("Mac-Win Bridge Companion")
        }
        .defaultSize(width: 380, height: 500)
        
        // ── Menu Bar Extra ──
        MenuBarExtra {
            VStack(alignment: .leading, spacing: 8) {
                // Connection status
                HStack {
                    Circle()
                        .fill(bridgeService.isConnected ? Color.green : Color.red)
                        .frame(width: 8, height: 8)
                    Text(bridgeService.isConnected
                         ? "接続: \(bridgeService.connectedHost ?? "Windows")"
                         : "未接続")
                        .font(.caption)
                }
                
                Divider()
                
                // Display mode
                HStack {
                    Image(systemName: "display.2")
                    Text("モード: \(bridgeService.displayMode == .mac ? "Mac配信" : "Windows")")
                        .font(.caption)
                }
                
                // KVM focus
                if bridgeService.kvmActive {
                    HStack {
                        Image(systemName: "keyboard.fill")
                        Text("KVM: \(bridgeService.isFocusOnMac ? "Mac操作中" : "Windows操作中")")
                            .font(.caption)
                    }
                }
                
                Divider()
                
                Button("ウィンドウを表示") {
                    NSApplication.shared.activate(ignoringOtherApps: true)
                    if let window = NSApplication.shared.windows.first {
                        window.makeKeyAndOrderFront(nil)
                    }
                }
                
                if bridgeService.isConnected {
                    Button("切断") {
                        bridgeService.disconnect()
                    }
                }
                
                Divider()
                
                Button("終了") {
                    NSApplication.shared.terminate(nil)
                }
                .keyboardShortcut("q")
            }
            .padding(8)
        } label: {
            HStack(spacing: 4) {
                Image(systemName: bridgeService.isConnected ? "link.circle.fill" : "link.circle")
                if bridgeService.isConnected {
                    Text(bridgeService.displayMode == .mac ? "🍎" : "🪟")
                        .font(.caption2)
                }
            }
        }
    }
}
