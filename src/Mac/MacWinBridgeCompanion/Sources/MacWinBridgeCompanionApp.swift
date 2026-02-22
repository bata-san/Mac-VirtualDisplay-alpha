// Mac-Win Bridge Companion: Entry point for macOS companion app.
// Receives video, audio, and input events from the Windows host.

import SwiftUI

@main
struct MacWinBridgeCompanionApp: App {
    @StateObject private var bridgeService = BridgeService()
    
    var body: some Scene {
        MenuBarExtra("Mac-Win Bridge", systemImage: bridgeService.isConnected ? "link.circle.fill" : "link.circle") {
            MenuBarView()
                .environmentObject(bridgeService)
        }
        .menuBarExtraStyle(.window)
        
        WindowGroup {
            ContentView()
                .environmentObject(bridgeService)
        }
        .defaultSize(width: 480, height: 600)
    }
}
