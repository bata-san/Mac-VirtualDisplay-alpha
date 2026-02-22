// Mac-Win Bridge Companion: Central bridge service managing all connections.

import Foundation
import Combine
import Network
import AppKit

/// Manages TCP connections from the Windows host and dispatches messages
/// to the appropriate handlers (video, audio, input).
@MainActor
class BridgeService: ObservableObject {
    
    // MARK: - Published State
    @Published var isConnected = false
    @Published var connectedHost: String?
    @Published var statusMessage = "Windowsホストからの接続を待機中..."
    @Published var displayMode: DisplayMode = .windows
    @Published var audioStreaming = false
    @Published var kvmActive = false
    @Published var isFocusOnMac = false
    @Published var audioPacketsReceived: Int64 = 0
    @Published var videoFramesSent: Int64 = 0
    
    // MARK: - Ports (must match Windows side)
    static let controlPort: UInt16 = 42100
    static let videoPort: UInt16 = 42101
    static let audioPort: UInt16 = 42102
    static let discoveryPort: UInt16 = 42099
    
    // MARK: - Services
    private var controlListener: NWListener?
    private var videoListener: NWListener?
    private var audioListener: NWListener?
    private var discoveryListener: NWListener?
    
    private var controlConnection: NWConnection?
    private var videoConnection: NWConnection?
    private var audioConnection: NWConnection?
    
    private let audioMixer = AudioMixer()
    private let inputInjector = InputInjector()
    let screenStreamer = ScreenStreamer()
    
    // MARK: - Lifecycle
    
    init() {
        // Wire up CursorReturn: when cursor leaves Mac, send to Windows
        inputInjector.onCursorReturn = { [weak self] edge, position in
            self?.sendCursorReturn(edge: edge, position: position)
        }
        startDiscoveryResponder()
        startListening()
    }
    
    /// Start listening on all ports for Windows host connections.
    func startListening() {
        statusMessage = "ポート \(Self.controlPort)-\(Self.audioPort) でリスニング中..."
        
        controlListener = createListener(port: Self.controlPort) { [weak self] conn in
            self?.controlConnection = conn
            self?.handleControlConnection(conn)
        }
        
        videoListener = createListener(port: Self.videoPort) { [weak self] conn in
            self?.videoConnection = conn
            self?.handleVideoConnection(conn)
        }
        
        audioListener = createListener(port: Self.audioPort) { [weak self] conn in
            self?.audioConnection = conn
            self?.handleAudioConnection(conn)
        }
    }
    
    /// Stop all listeners and connections.
    func disconnect() {
        controlConnection?.cancel()
        videoConnection?.cancel()
        audioConnection?.cancel()
        
        controlConnection = nil
        videoConnection = nil
        audioConnection = nil
        
        audioMixer.stop()
        
        isConnected = false
        connectedHost = nil
        audioStreaming = false
        kvmActive = false
        statusMessage = "切断しました"
    }
    
    // MARK: - Discovery Responder
    
    /// Respond to UDP discovery broadcasts from the Windows host.
    private func startDiscoveryResponder() {
        let params = NWParameters.udp
        params.allowLocalEndpointReuse = true
        
        discoveryListener = try? NWListener(using: params, on: NWEndpoint.Port(rawValue: Self.discoveryPort)!)
        discoveryListener?.newConnectionHandler = { connection in
            connection.start(queue: .main)
            Task { @MainActor [weak self] in
                self?.handleDiscoveryRequest(connection)
            }
        }
        discoveryListener?.start(queue: .main)
    }
    
    private func handleDiscoveryRequest(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1024) { data, _, _, _ in
            guard let data = data,
                  let message = String(data: data, encoding: .utf8),
                  message == "MACWINBRIDGE_DISCOVER" else { return }
            
            let response = "MACWINBRIDGE_HERE|\(Host.current().localizedName ?? "Mac")"
            if let responseData = response.data(using: .utf8) {
                connection.send(content: responseData, completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
    }
    
    // MARK: - Connection Handling
    
    private func createListener(port: UInt16, handler: @escaping (NWConnection) -> Void) -> NWListener? {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        
        guard let listener = try? NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!) else {
            statusMessage = "ポート \(port) のリスナー作成に失敗"
            return nil
        }
        
        listener.newConnectionHandler = { connection in
            connection.start(queue: .main)
            Task { @MainActor in
                handler(connection)
            }
        }
        
        listener.start(queue: .main)
        return listener
    }
    
    private func handleControlConnection(_ connection: NWConnection) {
        let endpoint = connection.endpoint
        isConnected = true
        connectedHost = "\(endpoint)"
        statusMessage = "Windows (\(endpoint)) から接続"
        
        receiveLoop(connection: connection) { [weak self] header, payload in
            self?.handleControlMessage(type: header.type, payload: payload)
        }
    }
    
    private func handleVideoConnection(_ connection: NWConnection) {
        // Mac sends video to Windows (ScreenStreamer); this port is outbound-only.
        // Streaming starts when Windows sends a displaySwitch(Mac) control message.
    }
    
    private func handleAudioConnection(_ connection: NWConnection) {
        audioStreaming = true
        
        receiveLoop(connection: connection) { [weak self] header, payload in
            guard let self = self else { return }
            
            switch header.type {
            case .audioConfig:
                // Parse audio config from Windows
                if let json = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] {
                    let sampleRate = json["SampleRate"] as? Int ?? 48000
                    let channels = json["Channels"] as? Int ?? 2
                    let bitsPerSample = json["BitsPerSample"] as? Int ?? 16
                    self.audioMixer.configure(sampleRate: sampleRate, channels: channels, bitsPerSample: bitsPerSample)
                    self.audioMixer.start()
                }
                
            case .audioData:
                self.audioPacketsReceived += 1
                self.audioMixer.receiveAudioData(payload)
                
            case .audioControl:
                // Handle audio routing changes, etc.
                break
                
            default:
                break
            }
        }
    }
    
    // MARK: - Message Processing
    
    private func handleControlMessage(type: MessageType, payload: Data) {
        switch type {
        case .handshake:
            // Windows sent handshake; send ack
            sendHandshakeAck()
            
        case .heartbeat:
            // Respond to heartbeat
            sendHeartbeatAck()
            
        case .displaySwitch:
            if let json = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
               let modeStr = json["Mode"] as? String {
                let newMode: DisplayMode = modeStr == "Mac" ? .mac : .windows
                displayMode = newMode
                
                // Start/stop screen streaming based on mode
                if newMode == .mac, let conn = videoConnection {
                    let width = json["Width"] as? Int ?? 1920
                    let height = json["Height"] as? Int ?? 1080
                    let fps = json["Fps"] as? Int ?? 60
                    let bitrate = json["Bitrate"] as? Int ?? 20_000_000
                    
                    screenStreamer.configure(width: width, height: height, fps: fps, bitrate: bitrate)
                    
                    // Configure InputInjector with Mac screen dimensions
                    if let screen = NSScreen.main {
                        let returnEdge: String
                        switch (json["MacEdge"] as? String) ?? "Right" {
                        case "Right": returnEdge = "Left"
                        case "Left": returnEdge = "Right"
                        case "Top": returnEdge = "Bottom"
                        case "Bottom": returnEdge = "Top"
                        default: returnEdge = "Left"
                        }
                        inputInjector.configure(
                            width: screen.frame.width,
                            height: screen.frame.height,
                            returnEdge: returnEdge)
                    }
                    
                    Task {
                        await self.screenStreamer.start(connection: conn)
                    }
                } else {
                    screenStreamer.stop()
                }
            }
            
        case .videoKeyRequest:
            screenStreamer.forceKeyFrame()
            
        case .audioConfig:
            if let json = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] {
                let sampleRate = json["SampleRate"] as? Int ?? 48000
                let channels = json["Channels"] as? Int ?? 2
                let bitsPerSample = json["BitsPerSample"] as? Int ?? 16
                audioMixer.configure(sampleRate: sampleRate, channels: channels, bitsPerSample: bitsPerSample)
            }
            
        case .mouseMove:
            guard payload.count >= 8 else { return }
            let x = payload.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
            let y = payload.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self) }
            inputInjector.injectMouseMove(x: Int(x), y: Int(y))
            kvmActive = true
            isFocusOnMac = true
            
        case .mouseButton:
            guard payload.count >= 4 else { return }
            let action = payload.withUnsafeBytes { $0.load(as: Int32.self) }
            inputInjector.injectMouseButton(action: Int(action))
            
        case .mouseScroll:
            guard payload.count >= 8 else { return }
            let isHorizontal = payload.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
            let delta = payload.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self) }
            inputInjector.injectMouseScroll(deltaX: isHorizontal != 0 ? Int(delta) : 0,
                               deltaY: isHorizontal == 0 ? Int(delta) : 0)
            
        case .keyDown:
            guard payload.count >= 4 else { return }
            let vkCode = payload.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
            inputInjector.injectKeyDown(vkCode: Int(vkCode))
            
        case .keyUp:
            guard payload.count >= 4 else { return }
            let vkCode = payload.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
            inputInjector.injectKeyUp(vkCode: Int(vkCode))
            
        case .cursorReturn:
            isFocusOnMac = false
            kvmActive = false
            
        case .kvmConfig:
            // Windows sent its screen dimensions for coordinate mapping
            break
            
        default:
            break
        }
    }
    
    /// Send CursorReturn to Windows to release KVM focus back.
    func sendCursorReturn(edge: String = "Left", position: Double = 0.5) {
        guard let connection = controlConnection else { return }
        
        // Build CursorReturn message: header (8 bytes) + no payload for simple return
        var packet = Data(capacity: 8)
        var msgType: UInt16 = MessageType.cursorReturn.rawValue  // 0x0303
        packet.append(Data(bytes: &msgType, count: 2))
        var flags: UInt16 = 0
        packet.append(Data(bytes: &flags, count: 2))
        var length: UInt32 = 0
        packet.append(Data(bytes: &length, count: 4))
        
        connection.send(content: packet, completion: .contentProcessed { _ in })
        isFocusOnMac = false
    }
    
    /// Send HandshakeAck to Windows.
    private func sendHandshakeAck() {
        guard let connection = controlConnection else { return }
        
        let ackPayload: [String: Any] = [
            "AppVersion": "0.1.0",
            "MachineName": Host.current().localizedName ?? "Mac",
            "ScreenWidth": Int(NSScreen.main?.frame.width ?? 2560),
            "ScreenHeight": Int(NSScreen.main?.frame.height ?? 1600)
        ]
        
        guard let jsonData = try? JSONSerialization.data(withJSONObject: ackPayload) else { return }
        
        var packet = Data(capacity: 8 + jsonData.count)
        var msgType: UInt16 = MessageType.handshakeAck.rawValue
        packet.append(Data(bytes: &msgType, count: 2))
        var flags: UInt16 = 0
        packet.append(Data(bytes: &flags, count: 2))
        var payloadLen: UInt32 = UInt32(jsonData.count)
        packet.append(Data(bytes: &payloadLen, count: 4))
        packet.append(jsonData)
        
        connection.send(content: packet, completion: .contentProcessed { _ in })
    }
    
    /// Respond to Windows heartbeat.
    private func sendHeartbeatAck() {
        guard let connection = controlConnection else { return }
        
        var packet = Data(capacity: 8)
        var msgType: UInt16 = MessageType.heartbeat.rawValue
        packet.append(Data(bytes: &msgType, count: 2))
        var flags: UInt16 = 0
        packet.append(Data(bytes: &flags, count: 2))
        var length: UInt32 = 0
        packet.append(Data(bytes: &length, count: 4))
        
        connection.send(content: packet, completion: .contentProcessed { _ in })
    }
    
    // MARK: - Receive Loop
    
    private func receiveLoop(connection: NWConnection,
                              handler: @escaping @MainActor (MessageHeader, Data) -> Void) {
        // Read 8-byte header
        connection.receive(minimumIncompleteLength: 8, maximumLength: 8) { [weak self] data, _, isComplete, error in
            guard let self = self else { return }
            guard let data = data, data.count == 8 else {
                if isComplete {
                    Task { @MainActor [weak self] in
                        self?.handleDisconnect()
                    }
                }
                return
            }
            
            let header = MessageHeader.deserialize(from: data)
            
            if header.payloadLength > 0 {
                // Read payload
                connection.receive(minimumIncompleteLength: Int(header.payloadLength),
                                   maximumLength: Int(header.payloadLength)) { [weak self] payload, _, _, _ in
                    if let payload = payload {
                        Task { @MainActor in
                            handler(header, payload)
                        }
                    }
                    // Continue reading
                    Task { @MainActor [weak self] in
                        self?.receiveLoop(connection: connection, handler: handler)
                    }
                }
            } else {
                Task { @MainActor [weak self] in
                    handler(header, Data())
                    self?.receiveLoop(connection: connection, handler: handler)
                }
            }
        }
    }
    
    private func handleDisconnect() {
        isConnected = false
        audioStreaming = false
        kvmActive = false
        isFocusOnMac = false
        screenStreamer.stop()
        statusMessage = "Windows ホストが切断されました"
        audioMixer.stop()
    }
}

// MARK: - Shared Types

enum DisplayMode: String {
    case windows = "Windows"
    case mac = "Mac"
}

enum MessageType: UInt16 {
    // Control (0x00xx)
    case handshake      = 0x0001
    case handshakeAck   = 0x0002
    case heartbeat      = 0x0003
    case disconnect     = 0x0004
    
    // Video (0x01xx)
    case videoFrame     = 0x0100
    case videoConfig    = 0x0101
    case displaySwitch  = 0x0102
    case displayStatus  = 0x0103
    case videoKeyRequest = 0x0104
    
    // Audio (0x02xx)
    case audioData      = 0x0200
    case audioConfig    = 0x0201
    case audioControl   = 0x0202
    
    // Input / KVM (0x03xx)
    case mouseMove      = 0x0300
    case mouseButton    = 0x0301
    case mouseScroll    = 0x0302
    case cursorReturn   = 0x0303
    case keyDown        = 0x0310
    case keyUp          = 0x0311
    case clipboardSync  = 0x0320
    case kvmConfig      = 0x0330
    
    case unknown        = 0xFFFF
}

struct MessageFlags: OptionSet {
    let rawValue: UInt16
    static let compressed = MessageFlags(rawValue: 1 << 0)
    static let encrypted  = MessageFlags(rawValue: 1 << 1)
    static let priority   = MessageFlags(rawValue: 1 << 2)
    static let keyFrame   = MessageFlags(rawValue: 1 << 3)
}

struct MessageHeader {
    let type: MessageType
    let flags: MessageFlags
    let payloadLength: UInt32
    
    static func deserialize(from data: Data) -> MessageHeader {
        let typeRaw = data.withUnsafeBytes { $0.load(fromByteOffset: 0, as: UInt16.self) }
        let flagsRaw = data.withUnsafeBytes { $0.load(fromByteOffset: 2, as: UInt16.self) }
        let length = data.withUnsafeBytes { $0.load(fromByteOffset: 4, as: UInt32.self) }
        
        return MessageHeader(
            type: MessageType(rawValue: typeRaw) ?? .unknown,
            flags: MessageFlags(rawValue: flagsRaw),
            payloadLength: length
        )
    }
}
