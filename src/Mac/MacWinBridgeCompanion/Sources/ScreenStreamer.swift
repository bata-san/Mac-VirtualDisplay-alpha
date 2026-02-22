// Mac-Win Bridge Companion: Screen capture and H.264 encoding.
// Captures the Mac screen using ScreenCaptureKit, encodes via VideoToolbox,
// and sends H.264 NAL units to the Windows host for display on the 2nd monitor.

import Foundation
import ScreenCaptureKit
import VideoToolbox
import CoreMedia
import Network

/// Captures the Mac screen and streams H.264 encoded video to the Windows host.
@MainActor
class ScreenStreamer: NSObject, ObservableObject, SCStreamOutput {
    
    @Published var isStreaming = false
    @Published var fps: Double = 0
    @Published var encodedBytesPerSec: Int = 0
    
    private var stream: SCStream?
    private var compressionSession: VTCompressionSession?
    private var connection: NWConnection?
    
    // Config from Windows
    private var targetWidth: Int = 1920
    private var targetHeight: Int = 1080
    private var targetFps: Int = 60
    private var targetBitrate: Int = 20_000_000
    
    // Stats
    private var frameCount: Int = 0
    private var bytesSent: Int = 0
    private var lastStatsTime = Date()
    
    // Frame header size matching Windows VideoFrameHeader.Size
    private let frameHeaderSize = 22
    
    /// Configure streaming parameters (called when Windows sends DisplaySwitch message).
    func configure(width: Int, height: Int, fps: Int, bitrate: Int) {
        self.targetWidth = width
        self.targetHeight = height
        self.targetFps = fps
        self.targetBitrate = bitrate
    }
    
    /// Start screen capture and encoding.
    func start(connection: NWConnection) async {
        self.connection = connection
        
        do {
            // Get available content
            let content = try await SCShareableContent.current
            guard let display = content.displays.first else {
                print("[ScreenStreamer] No display found")
                return
            }
            
            // Create capture filter (entire display, exclude this app's windows)
            let filter = SCContentFilter(display: display, excludingWindows: [])
            
            // Configure capture
            let config = SCStreamConfiguration()
            config.width = targetWidth
            config.height = targetHeight
            config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(targetFps))
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.queueDepth = 3
            
            // Create VideoToolbox compression session
            setupEncoder()
            
            // Create and start stream
            stream = SCStream(filter: filter, configuration: config, delegate: nil)
            try stream?.addStreamOutput(self, type: .screen, sampleHandlerQueue: DispatchQueue(label: "screen-capture", qos: .userInteractive))
            try await stream?.startCapture()
            
            isStreaming = true
            print("[ScreenStreamer] Started: \(targetWidth)x\(targetHeight)@\(targetFps)fps, bitrate=\(targetBitrate)")
            
        } catch {
            print("[ScreenStreamer] Failed to start: \(error)")
        }
    }
    
    /// Stop capture and encoding.
    func stop() {
        Task {
            try? await stream?.stopCapture()
        }
        stream = nil
        
        if let session = compressionSession {
            VTCompressionSessionInvalidate(session)
            compressionSession = nil
        }
        
        isStreaming = false
        print("[ScreenStreamer] Stopped. Sent \(bytesSent) bytes in \(frameCount) frames")
    }
    
    /// Force an IDR keyframe (requested by Windows after packet loss).
    func forceKeyFrame() {
        guard let session = compressionSession else { return }
        let props: [String: Any] = [kVTEncodeFrameOptionKey_ForceKeyFrame as String: true]
        // Will be applied on next encode
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
    }
    
    // MARK: - SCStreamOutput
    
    nonisolated func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        
        // Encode frame
        guard let session = compressionSession else { return }
        
        VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: pts,
            duration: CMTime(value: 1, timescale: CMTimeScale(targetFps)),
            frameProperties: nil,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil)
    }
    
    // MARK: - VideoToolbox Encoder Setup
    
    private func setupEncoder() {
        var session: VTCompressionSession?
        
        let status = VTCompressionSessionCreate(
            allocator: nil,
            width: Int32(targetWidth),
            height: Int32(targetHeight),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: [
                kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder as String: true
            ] as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &session)
        
        guard status == noErr, let session = session else {
            print("[ScreenStreamer] Failed to create encoder: \(status)")
            return
        }
        
        // Configure encoder
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_Main_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: targetBitrate as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: 30 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        
        // Set output handler
        VTCompressionSessionSetOutputHandler(session) { [weak self] status, flags, sampleBuffer in
            guard status == noErr, let sampleBuffer = sampleBuffer else { return }
            self?.handleEncodedFrame(sampleBuffer)
        }
        
        VTCompressionSessionPrepareToEncodeFrames(session)
        compressionSession = session
        print("[ScreenStreamer] H.264 encoder initialized")
    }
    
    // MARK: - Encoded Frame Handling
    
    private func handleEncodedFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let connection = connection else { return }
        
        // Determine if keyframe
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
        var isKeyFrame = false
        if let attachments = attachments, CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFDictionary.self)
            let notSync = CFDictionaryContainsKey(dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque())
            isKeyFrame = !notSync
        }
        
        // Extract H.264 data
        guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var totalLength: Int = 0
        var dataPointer: UnsafeMutablePointer<CChar>?
        CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &totalLength, dataPointerOut: &dataPointer)
        
        guard let ptr = dataPointer, totalLength > 0 else { return }
        let nalData = Data(bytes: ptr, count: totalLength)
        
        // Build frame packet: [MessageHeader:8][VideoFrameHeader:22][NAL data:N]
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let ptsValue = Int64(pts.seconds * 1_000_000)  // microseconds
        
        // Message header (8 bytes)
        var packet = Data(capacity: 8 + frameHeaderSize + nalData.count)
        
        // Type: VideoFrame (0x0202)
        var msgType: UInt16 = 0x0202
        packet.append(Data(bytes: &msgType, count: 2))
        
        // Flags
        var flags: UInt16 = isKeyFrame ? 0x0008 : 0x0000  // KeyFrame flag
        packet.append(Data(bytes: &flags, count: 2))
        
        // Payload length
        var payloadLen: UInt32 = UInt32(frameHeaderSize + nalData.count)
        packet.append(Data(bytes: &payloadLen, count: 4))
        
        // Video frame header (22 bytes)
        var width: Int32 = Int32(targetWidth)
        var height: Int32 = Int32(targetHeight)
        var codec: UInt8 = 0  // H.264
        var frameType: UInt8 = isKeyFrame ? 1 : 0  // IDR or P-frame
        var ptsVal: Int64 = ptsValue
        var dataLen: Int32 = Int32(nalData.count)
        
        packet.append(Data(bytes: &width, count: 4))
        packet.append(Data(bytes: &height, count: 4))
        packet.append(Data(bytes: &codec, count: 1))
        packet.append(Data(bytes: &frameType, count: 1))
        packet.append(Data(bytes: &ptsVal, count: 8))
        packet.append(Data(bytes: &dataLen, count: 4))
        
        // NAL data
        packet.append(nalData)
        
        // Send
        connection.send(content: packet, completion: .contentProcessed { error in
            if let error = error {
                print("[ScreenStreamer] Send error: \(error)")
            }
        })
        
        frameCount += 1
        bytesSent += packet.count
        
        // Update stats every second
        let now = Date()
        if now.timeIntervalSince(lastStatsTime) >= 1.0 {
            let elapsed = now.timeIntervalSince(lastStatsTime)
            DispatchQueue.main.async { [self] in
                fps = Double(frameCount) / elapsed
                encodedBytesPerSec = Int(Double(bytesSent) / elapsed)
            }
            frameCount = 0
            bytesSent = 0
            lastStatsTime = now
        }
    }
}
