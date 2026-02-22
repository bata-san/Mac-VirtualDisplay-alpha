// swift-tools-version: 5.9
// Mac-Win Bridge Companion App for macOS

import PackageDescription

let package = Package(
    name: "MacWinBridgeCompanion",
    platforms: [
        .macOS(.v14)
    ],
    dependencies: [],
    targets: [
        .executableTarget(
            name: "MacWinBridgeCompanion",
            dependencies: [],
            path: "Sources"
        ),
    ]
)
