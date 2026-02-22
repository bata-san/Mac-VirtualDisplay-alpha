// Mac-Win Bridge Companion: Video frame receiver and full-screen display.

import Foundation
import CoreGraphics
import AppKit

/// Receives video frames from the Windows host and renders them.
/// When in Mac mode, this takes over the designated display.
class VideoReceiver: ObservableObject {
    @Published var lastFrameWidth: Int = 0
    @Published var lastFrameHeight: Int = 0
    @Published var framesReceived: Int64 = 0
    
    private var previousFrame: Data?
    private var fullScreenWindow: NSWindow?
    private var imageView: NSImageView?
    
    /// Process an incoming video frame packet.
    func processFrame(payload: Data, flags: MessageFlags) {
        guard payload.count >= 16 else { return }
        
        // Parse frame header (16 bytes)
        let width = payload.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
        let height = payload.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self) }
        let stride = payload.withUnsafeBytes { $0.load(fromByteOffset: 8, as: Int32.self) }
        // let frameNumber = payload.withUnsafeBytes { $0.load(fromByteOffset: 12, as: Int32.self) }
        
        let pixelData = payload.advanced(by: 16)
        
        var frameData: Data
        
        if flags.contains(.keyFrame) {
            // Full key frame
            frameData = pixelData
            previousFrame = pixelData
        } else if flags.contains(.compressed), let prev = previousFrame {
            // Delta frame: XOR with previous
            frameData = xorDecode(delta: pixelData, previous: prev)
            previousFrame = frameData
        } else {
            frameData = pixelData
        }
        
        lastFrameWidth = Int(width)
        lastFrameHeight = Int(height)
        framesReceived += 1
        
        // Render frame
        renderFrame(data: frameData, width: Int(width), height: Int(height), stride: Int(stride))
    }
    
    /// Show full-screen video window on the specified display.
    func showFullScreen(on screen: NSScreen) {
        let window = NSWindow(
            contentRect: screen.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false,
            screen: screen
        )
        window.level = .screenSaver
        window.backgroundColor = .black
        window.collectionBehavior = [.fullScreenPrimary, .canJoinAllSpaces]
        
        let view = NSImageView(frame: screen.frame)
        view.imageScaling = .scaleProportionallyUpOrDown
        window.contentView = view
        
        window.makeKeyAndOrderFront(nil)
        window.toggleFullScreen(nil)
        
        fullScreenWindow = window
        imageView = view
    }
    
    /// Hide the full-screen video window.
    func hideFullScreen() {
        fullScreenWindow?.close()
        fullScreenWindow = nil
        imageView = nil
    }
    
    // MARK: - Private
    
    private func renderFrame(data: Data, width: Int, height: Int, stride: Int) {
        guard let imageView = imageView else { return }
        
        // Create CGImage from BGRA pixel data
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        
        data.withUnsafeBytes { ptr in
            guard let baseAddress = ptr.baseAddress else { return }
            
            if let context = CGContext(
                data: UnsafeMutableRawPointer(mutating: baseAddress),
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: stride,
                space: colorSpace,
                bitmapInfo: bitmapInfo.rawValue
            ), let cgImage = context.makeImage() {
                let nsImage = NSImage(cgImage: cgImage, size: NSSize(width: width, height: height))
                
                DispatchQueue.main.async {
                    imageView.image = nsImage
                }
            }
        }
    }
    
    private func xorDecode(delta: Data, previous: Data) -> Data {
        var result = Data(count: min(delta.count, previous.count))
        let count = result.count
        
        result.withUnsafeMutableBytes { resultPtr in
            delta.withUnsafeBytes { deltaPtr in
                previous.withUnsafeBytes { prevPtr in
                    guard let r = resultPtr.baseAddress?.assumingMemoryBound(to: UInt8.self),
                          let d = deltaPtr.baseAddress?.assumingMemoryBound(to: UInt8.self),
                          let p = prevPtr.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
                    
                    for i in 0..<count {
                        r[i] = d[i] ^ p[i]
                    }
                }
            }
        }
        
        return result
    }
}
