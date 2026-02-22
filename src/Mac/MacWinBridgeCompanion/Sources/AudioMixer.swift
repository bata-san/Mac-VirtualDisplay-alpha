// Mac-Win Bridge Companion: Audio playback mixer using AVAudioEngine.
// Receives PCM audio from Windows and plays it through Mac speakers.

import Foundation
import AVFoundation

/// Plays PCM audio received from Windows through the Mac's audio output.
/// Uses AVAudioEngine with a jitter buffer to handle network timing variations.
class AudioMixer: ObservableObject {
    
    @Published var isPlaying = false
    @Published var volume: Float = 1.0
    @Published var isMuted = false
    
    private var engine: AVAudioEngine?
    private var playerNode: AVAudioPlayerNode?
    private var audioFormat: AVAudioFormat?
    
    // Config
    private var sampleRate: Double = 48000
    private var channels: AVAudioChannelCount = 2
    private var bitsPerSample: Int = 16
    
    // Jitter buffer
    private let bufferQueue = DispatchQueue(label: "audio-buffer", qos: .userInteractive)
    private var pendingBuffers: [AVAudioPCMBuffer] = []
    private let maxPendingBuffers = 5
    
    // Stats
    private var buffersPlayed: Int = 0
    private var buffersDropped: Int = 0
    
    /// Configure audio format from Windows AudioConfig message.
    func configure(sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.sampleRate = Double(sampleRate)
        self.channels = AVAudioChannelCount(channels)
        self.bitsPerSample = bitsPerSample
        
        audioFormat = AVAudioFormat(
            commonFormat: bitsPerSample == 16 ? .pcmFormatInt16 : .pcmFormatFloat32,
            sampleRate: self.sampleRate,
            channels: self.channels,
            interleaved: true)
        
        print("[AudioMixer] Configured: \(sampleRate)Hz, \(channels)ch, \(bitsPerSample)bit")
    }
    
    /// Start the audio engine.
    func start() {
        engine = AVAudioEngine()
        playerNode = AVAudioPlayerNode()
        
        guard let engine = engine, let player = playerNode, let format = audioFormat else { return }
        
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
        engine.mainMixerNode.outputVolume = volume
        
        do {
            try engine.start()
            player.play()
            isPlaying = true
            print("[AudioMixer] Started")
        } catch {
            print("[AudioMixer] Failed to start: \(error)")
        }
    }
    
    /// Stop the audio engine.
    func stop() {
        playerNode?.stop()
        engine?.stop()
        engine = nil
        playerNode = nil
        isPlaying = false
        print("[AudioMixer] Stopped. Played: \(buffersPlayed), Dropped: \(buffersDropped)")
    }
    
    /// Receive PCM audio data from network (timestamp + raw PCM).
    func receiveAudioData(_ data: Data) {
        guard let format = audioFormat, let player = playerNode else { return }
        guard data.count > 8 else { return }
        
        // Skip timestamp (first 8 bytes)
        let pcmData = data.dropFirst(8)
        
        let bytesPerFrame = Int(format.streamDescription.pointee.mBytesPerFrame)
        guard bytesPerFrame > 0 else { return }
        let frameCount = AVAudioFrameCount(pcmData.count / bytesPerFrame)
        guard frameCount > 0 else { return }
        
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount) else { return }
        buffer.frameLength = frameCount
        
        // Copy PCM data into buffer
        pcmData.withUnsafeBytes { ptr in
            guard let baseAddr = ptr.baseAddress else { return }
            memcpy(buffer.audioBufferList.pointee.mBuffers.mData,
                   baseAddr, pcmData.count)
        }
        
        // Simple jitter buffer: drop oldest if too many pending
        bufferQueue.async { [self] in
            if pendingBuffers.count >= maxPendingBuffers {
                pendingBuffers.removeFirst()
                buffersDropped += 1
            }
            pendingBuffers.append(buffer)
            
            // Schedule buffer for playback
            player.scheduleBuffer(buffer)
            buffersPlayed += 1
        }
    }
    
    /// Set volume (0.0 – 1.0).
    func setVolume(_ vol: Float) {
        volume = max(0, min(1, vol))
        engine?.mainMixerNode.outputVolume = isMuted ? 0 : volume
    }
    
    /// Toggle mute.
    func toggleMute() {
        isMuted.toggle()
        engine?.mainMixerNode.outputVolume = isMuted ? 0 : volume
    }
}
