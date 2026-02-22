// Mac-Win Bridge Companion: Audio mixer – receives Windows audio and mixes with Mac audio.

import Foundation
import AVFoundation
import CoreAudio

/// Receives PCM audio data from the Windows host and plays it back,
/// mixing with macOS native audio output.
class AudioMixer {
    private var audioEngine: AVAudioEngine?
    private var playerNode: AVAudioPlayerNode?
    private var mixerNode: AVAudioMixerNode?
    
    private var audioFormat: AVAudioFormat?
    private var isRunning = false
    
    // Buffer management
    private let bufferQueue = DispatchQueue(label: "audio-mixer", qos: .userInteractive)
    
    // Default format: 48kHz, 16-bit, stereo (matches Windows side)
    private var sampleRate: Double = 48000
    private var channels: UInt32 = 2
    
    /// Configure audio format based on config received from Windows.
    func configure(sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.sampleRate = Double(sampleRate)
        self.channels = UInt32(channels)
    }
    
    /// Start the audio playback engine.
    func start() {
        guard !isRunning else { return }
        
        audioEngine = AVAudioEngine()
        playerNode = AVAudioPlayerNode()
        
        guard let engine = audioEngine, let player = playerNode else { return }
        
        // Create format for incoming Windows audio (16-bit PCM)
        audioFormat = AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: sampleRate,
            channels: channels,
            interleaved: true
        )
        
        // Attach player to engine
        engine.attach(player)
        
        // Connect player → main mixer (this mixes with Mac's own audio)
        if let format = audioFormat {
            engine.connect(player, to: engine.mainMixerNode, format: format)
        }
        
        do {
            try engine.start()
            player.play()
            isRunning = true
            print("[AudioMixer] Engine started: \(sampleRate)Hz, \(channels)ch")
        } catch {
            print("[AudioMixer] Failed to start engine: \(error)")
        }
    }
    
    /// Stop the audio engine.
    func stop() {
        playerNode?.stop()
        audioEngine?.stop()
        isRunning = false
    }
    
    /// Receive and schedule a PCM audio packet from the Windows host.
    func receiveAudioPacket(_ data: Data) {
        guard isRunning,
              let player = playerNode,
              let format = audioFormat else { return }
        
        bufferQueue.async {
            // Skip 8-byte timestamp header
            guard data.count > 8 else { return }
            let audioData = data.advanced(by: 8)
            
            let frameCount = UInt32(audioData.count) / (format.streamDescription.pointee.mBytesPerFrame)
            guard frameCount > 0 else { return }
            
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount) else { return }
            buffer.frameLength = frameCount
            
            // Copy PCM data into the buffer
            audioData.withUnsafeBytes { srcPtr in
                guard let src = srcPtr.baseAddress else { return }
                if let dest = buffer.int16ChannelData {
                    // Interleaved: copy directly to channel 0 buffer
                    memcpy(dest[0], src, min(audioData.count, Int(frameCount) * Int(format.streamDescription.pointee.mBytesPerFrame)))
                }
            }
            
            // Schedule buffer for playback
            player.scheduleBuffer(buffer, completionHandler: nil)
        }
    }
}
