// Mac-Win Bridge Companion: Screen capture and H.264 encoding.
// Captures the Mac screen using ScreenCaptureKit, encodes via VideoToolbox,
// and sends H.264 NAL units to the Windows host for display on the 2nd monitor.

import Foundation
import ScreenCaptureKit
import VideoToolbox
import CoreMedia
import Network

// MARK: - EncoderState
// Holds all mutable encoder state in an @unchecked Sendable class so it can be safely
// accessed from both the @MainActor ScreenStreamer and the nonisolated SCStreamOutput
// callback without actor-isolation errors.
private final class EncoderState: @unchecked Sendable {
    var compressionSession: VTCompressionSession?
    var connection: NWConnection?

    // Config
    var targetWidth: Int  = 1920
    var targetHeight: Int = 1080
    var targetFps: Int    = 60
    var targetBitrate: Int = 20_000_000
    let frameHeaderSize    = 22

    // Stats (written on encode queue, read on main via onStats callback)
    var frameCount: Int   = 0
    var bytesSent: Int    = 0
    var lastStatsTime     = Date()

    // Called on main thread to propagate stats to @Published properties
    var onStats: ((Double, Int) -> Void)?

    // MARK: Encoded frame dispatch (called from C callback on VideoToolbox queue)
    func handleEncodedFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let connection else { return }

        // Keyframe detection
        var isKeyFrame = false
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false),
           CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFDictionary.self)
            isKeyFrame = !CFDictionaryContainsKey(
                dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque())
        }

        guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var totalLength = 0
        var dataPointer: UnsafeMutablePointer<CChar>?
        CMBlockBufferGetDataPointer(
            dataBuffer, atOffset: 0,
            lengthAtOffsetOut: nil, totalLengthOut: &totalLength,
            dataPointerOut: &dataPointer)
        guard let ptr = dataPointer, totalLength > 0 else { return }
        let nalData = Data(bytes: ptr, count: totalLength)

        let pts      = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let ptsValue = Int64(pts.seconds * 1_000_000) // microseconds

        // Build packet: [MessageHeader:8][VideoFrameHeader:22][NAL data:N]
        var packet = Data(capacity: 8 + frameHeaderSize + nalData.count)

        // Message header (8 bytes)
        var msgType: UInt16  = 0x0100  // VideoFrame
        var flags: UInt16    = isKeyFrame ? 0x0008 : 0x0000
        var payloadLen: UInt32 = UInt32(frameHeaderSize + nalData.count)
        packet.append(Data(bytes: &msgType,    count: 2))
        packet.append(Data(bytes: &flags,      count: 2))
        packet.append(Data(bytes: &payloadLen, count: 4))

        // VideoFrameHeader (22 bytes)
        var w: Int32   = Int32(targetWidth)
        var h: Int32   = Int32(targetHeight)
        var codec: UInt8 = 0
        var ftype: UInt8 = isKeyFrame ? 1 : 0
        var ptsVal: Int64  = ptsValue
        var dLen: Int32    = Int32(nalData.count)
        packet.append(Data(bytes: &w,     count: 4))
        packet.append(Data(bytes: &h,     count: 4))
        packet.append(Data(bytes: &codec, count: 1))
        packet.append(Data(bytes: &ftype, count: 1))
        packet.append(Data(bytes: &ptsVal, count: 8))
        packet.append(Data(bytes: &dLen,  count: 4))
        packet.append(nalData)

        connection.send(content: packet, completion: .contentProcessed { _ in })

        frameCount += 1
        bytesSent  += packet.count

        let now = Date()
        if now.timeIntervalSince(lastStatsTime) >= 1.0 {
            let elapsed = now.timeIntervalSince(lastStatsTime)
            let fpsVal  = Double(frameCount) / elapsed
            let bpsVal  = Int(Double(bytesSent) / elapsed)
            let cb      = onStats
            DispatchQueue.main.async { cb?(fpsVal, bpsVal) }
            frameCount    = 0
            bytesSent     = 0
            lastStatsTime = now
        }
    }
}

// MARK: - C-compatible encoder output callback
// Top-level function with no captures → can be used as a C function pointer
// for VTCompressionSessionCreate's outputCallback parameter.

private func _vtEncoderCallback(
    _ refCon: UnsafeMutableRawPointer?,
    _ sourceRefCon: UnsafeMutableRawPointer?,
    _ status: OSStatus,
    _ flags: VTEncodeInfoFlags,
    _ sampleBuffer: CMSampleBuffer?
) {
    guard status == noErr, let sampleBuffer, let refCon else { return }
    Unmanaged<EncoderState>.fromOpaque(refCon)
        .takeUnretainedValue()
        .handleEncodedFrame(sampleBuffer)
}

// MARK: - ScreenStreamer

/// Captures the Mac screen and streams H.264 encoded video to the Windows host.
@MainActor
class ScreenStreamer: NSObject, ObservableObject, SCStreamOutput {

    @Published var isStreaming = false
    @Published var fps: Double = 0
    @Published var encodedBytesPerSec: Int = 0

    private var captureStream: SCStream?

    // nonisolated let: EncoderState is @unchecked Sendable, so the nonisolated
    // SCStreamOutput.stream(_:didOutputSampleBuffer:of:) method can access it freely.
    nonisolated private let encoder = EncoderState()

    // MARK: - Public API

    /// Configure streaming parameters (called when Windows sends DisplaySwitch message).
    func configure(width: Int, height: Int, fps: Int, bitrate: Int) {
        encoder.targetWidth   = width
        encoder.targetHeight  = height
        encoder.targetFps     = fps
        encoder.targetBitrate = bitrate
    }

    /// Start screen capture and encoding, sending data over `connection`.
    func start(connection: NWConnection) async {
        encoder.connection = connection
        encoder.onStats = { [weak self] fpsVal, bpsVal in
            self?.fps = fpsVal
            self?.encodedBytesPerSec = bpsVal
        }

        do {
            let content = try await SCShareableContent.current
            guard let display = content.displays.first else {
                print("[ScreenStreamer] No display found")
                return
            }

            let filter = SCContentFilter(display: display, excludingWindows: [])

            let config = SCStreamConfiguration()
            config.width    = encoder.targetWidth
            config.height   = encoder.targetHeight
            config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(encoder.targetFps))
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.queueDepth  = 3

            setupEncoder()

            captureStream = SCStream(filter: filter, configuration: config, delegate: nil)
            try captureStream?.addStreamOutput(
                self, type: .screen,
                sampleHandlerQueue: DispatchQueue(label: "screen-capture", qos: .userInteractive))
            try await captureStream?.startCapture()

            isStreaming = true
            print("[ScreenStreamer] Started: \(encoder.targetWidth)x\(encoder.targetHeight)@\(encoder.targetFps)fps")

        } catch {
            print("[ScreenStreamer] Failed to start: \(error)")
        }
    }

    /// Stop capture and invalidate the encoder session.
    func stop() {
        Task { try? await captureStream?.stopCapture() }
        captureStream = nil

        if let session = encoder.compressionSession {
            VTCompressionSessionInvalidate(session)
            encoder.compressionSession = nil
        }

        isStreaming = false
        print("[ScreenStreamer] Stopped. Sent \(encoder.bytesSent) bytes in \(encoder.frameCount) frames")
    }

    /// Force an IDR keyframe on the next encode cycle (called after packet loss).
    func forceKeyFrame() {
        guard let session = encoder.compressionSession else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
    }

    // MARK: - SCStreamOutput
    // Protocol requires nonisolated. We access `encoder` (nonisolated let, @unchecked Sendable)
    // so no actor boundary is crossed.

    nonisolated func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of type: SCStreamOutputType
    ) {
        guard type == .screen else { return }
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        guard let session = encoder.compressionSession else { return }

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: pts,
            duration: CMTime(value: 1, timescale: CMTimeScale(encoder.targetFps)),
            frameProperties: nil,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil)
    }

    // MARK: - VideoToolbox Encoder Setup
    // Passes a C-compatible callback (_vtEncoderCallback) directly to
    // VTCompressionSessionCreate, avoiding the deprecated
    // VTCompressionSessionSetOutputHandler.

    private func setupEncoder() {
        var session: VTCompressionSession?
        let refCon = Unmanaged.passUnretained(encoder).toOpaque()

        let status = VTCompressionSessionCreate(
            allocator: nil,
            width:  Int32(encoder.targetWidth),
            height: Int32(encoder.targetHeight),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: [
                kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder as String: true
            ] as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: _vtEncoderCallback,   // C function pointer, zero captures
            refcon: refCon,
            compressionSessionOut: &session)

        guard status == noErr, let session else {
            print("[ScreenStreamer] Failed to create encoder: \(status)")
            return
        }

        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime,
                             value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel,
                             value: kVTProfileLevel_H264_Main_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate,
                             value: encoder.targetBitrate as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval,
                             value: 30 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering,
                             value: kCFBooleanFalse)

        VTCompressionSessionPrepareToEncodeFrames(session)
        encoder.compressionSession = session
        print("[ScreenStreamer] H.264 encoder initialized")
    }
}