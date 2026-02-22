// Mac-Win Bridge Companion: Input injection on macOS.
// Receives mouse/keyboard events from Windows and injects them as CGEvents.

import Foundation
import CoreGraphics

/// Injects mouse and keyboard events received from Windows into macOS.
/// Handles coordinate scaling and edge detection for CursorReturn.
class InputInjector: ObservableObject {
    
    @Published var isActive = false
    
    private var screenWidth: CGFloat = 0
    private var screenHeight: CGFloat = 0
    private var lastMouseX: CGFloat = 0
    private var lastMouseY: CGFloat = 0
    
    // Callback to send CursorReturn to Windows
    var onCursorReturn: ((String, Double) -> Void)?
    
    // Which edge of Mac corresponds to Windows (opposite of Windows' MacEdge setting)
    // If Windows has MacEdge=Right, cursor enters Mac from Left, so return edge is Left
    private var returnEdge: String = "Left"
    
    /// Configure the injector with Mac screen dimensions.
    func configure(width: CGFloat, height: CGFloat, returnEdge: String = "Left") {
        self.screenWidth = width
        self.screenHeight = height
        self.returnEdge = returnEdge
        isActive = true
        print("[InputInjector] Configured: \(Int(width))×\(Int(height)), return edge: \(returnEdge)")
    }
    
    // MARK: - Mouse Injection
    
    func injectMouseMove(x: Int, y: Int) {
        let cx = CGFloat(x)
        let cy = CGFloat(y)
        
        // Check if cursor left the Mac screen (should return to Windows)
        if shouldReturnToWindows(x: cx, y: cy) {
            let position = calculateReturnPosition(x: cx, y: cy)
            onCursorReturn?(returnEdge, position)
            return
        }
        
        // Clamp to screen
        let clampedX = min(max(cx, 0), screenWidth - 1)
        let clampedY = min(max(cy, 0), screenHeight - 1)
        
        lastMouseX = clampedX
        lastMouseY = clampedY
        
        let point = CGPoint(x: clampedX, y: clampedY)
        if let event = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved,
                               mouseCursorPosition: point, mouseButton: .left) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    func injectMouseButton(action: Int) {
        let point = CGPoint(x: lastMouseX, y: lastMouseY)
        
        let (eventType, button): (CGEventType, CGMouseButton) = switch action {
        case 1: (.leftMouseDown,  .left)
        case 2: (.leftMouseUp,    .left)
        case 3: (.rightMouseDown, .right)
        case 4: (.rightMouseUp,   .right)
        case 5: (.otherMouseDown, .center)
        case 6: (.otherMouseUp,   .center)
        default: return
        }
        
        if let event = CGEvent(mouseEventSource: nil, mouseType: eventType,
                               mouseCursorPosition: point, mouseButton: button) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    func injectMouseScroll(deltaX: Int, deltaY: Int) {
        if let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel,
                               wheelCount: 2,
                               wheel1: Int32(deltaY / 120),
                               wheel2: Int32(deltaX / 120)) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    // MARK: - Keyboard Injection
    
    func injectKeyDown(vkCode: Int) {
        guard let macKeyCode = windowsVKToMacKeyCode(vkCode) else { return }
        if let event = CGEvent(keyboardEventSource: nil, virtualKey: macKeyCode, keyDown: true) {
            applyModifiers(event: event, vkCode: vkCode)
            event.post(tap: .cghidEventTap)
        }
    }
    
    func injectKeyUp(vkCode: Int) {
        guard let macKeyCode = windowsVKToMacKeyCode(vkCode) else { return }
        if let event = CGEvent(keyboardEventSource: nil, virtualKey: macKeyCode, keyDown: false) {
            applyModifiers(event: event, vkCode: vkCode)
            event.post(tap: .cghidEventTap)
        }
    }
    
    // MARK: - Edge Detection (CursorReturn)
    
    private func shouldReturnToWindows(x: CGFloat, y: CGFloat) -> Bool {
        switch returnEdge {
        case "Left":   return x < 0
        case "Right":  return x >= screenWidth
        case "Top":    return y < 0
        case "Bottom": return y >= screenHeight
        default:       return false
        }
    }
    
    private func calculateReturnPosition(x: CGFloat, y: CGFloat) -> Double {
        switch returnEdge {
        case "Left", "Right":
            return Double(y / screenHeight)  // vertical position as 0.0–1.0
        case "Top", "Bottom":
            return Double(x / screenWidth)   // horizontal position as 0.0–1.0
        default:
            return 0.5
        }
    }
    
    // MARK: - Key Mapping (Windows VK → macOS CGKeyCode)
    
    private func windowsVKToMacKeyCode(_ vk: Int) -> CGKeyCode? {
        return vkToMacMap[vk]
    }
    
    private func applyModifiers(event: CGEvent, vkCode: Int) {
        // Modifier keys need special flag handling
        var flags = event.flags
        switch vkCode {
        case 0x10, 0xA0, 0xA1: flags.insert(.maskShift)
        case 0x11, 0xA2, 0xA3: flags.insert(.maskControl)
        case 0x12, 0xA4, 0xA5: flags.insert(.maskAlternate)
        case 0x5B, 0x5C:       flags.insert(.maskCommand)  // Win key → Cmd
        default: break
        }
        event.flags = flags
    }
    
    // Comprehensive VK → macOS key code mapping (US + JIS common keys)
    private let vkToMacMap: [Int: CGKeyCode] = [
        // Letters
        0x41: 0x00, // A
        0x42: 0x0B, // B
        0x43: 0x08, // C
        0x44: 0x02, // D
        0x45: 0x0E, // E
        0x46: 0x03, // F
        0x47: 0x05, // G
        0x48: 0x04, // H
        0x49: 0x22, // I
        0x4A: 0x26, // J
        0x4B: 0x28, // K
        0x4C: 0x25, // L
        0x4D: 0x2E, // M
        0x4E: 0x2D, // N
        0x4F: 0x1F, // O
        0x50: 0x23, // P
        0x51: 0x0C, // Q
        0x52: 0x0F, // R
        0x53: 0x01, // S
        0x54: 0x11, // T
        0x55: 0x20, // U
        0x56: 0x09, // V
        0x57: 0x0D, // W
        0x58: 0x07, // X
        0x59: 0x10, // Y
        0x5A: 0x06, // Z
        
        // Numbers
        0x30: 0x1D, // 0
        0x31: 0x12, // 1
        0x32: 0x13, // 2
        0x33: 0x14, // 3
        0x34: 0x15, // 4
        0x35: 0x17, // 5
        0x36: 0x16, // 6
        0x37: 0x1A, // 7
        0x38: 0x1C, // 8
        0x39: 0x19, // 9
        
        // Function keys
        0x70: 0x7A, // F1
        0x71: 0x78, // F2
        0x72: 0x63, // F3
        0x73: 0x76, // F4
        0x74: 0x60, // F5
        0x75: 0x61, // F6
        0x76: 0x62, // F7
        0x77: 0x64, // F8
        0x78: 0x65, // F9
        0x79: 0x6D, // F10
        0x7A: 0x67, // F11
        0x7B: 0x6F, // F12
        
        // Special keys
        0x08: 0x33, // Backspace → Delete
        0x09: 0x30, // Tab
        0x0D: 0x24, // Enter → Return
        0x1B: 0x35, // Escape
        0x20: 0x31, // Space
        0x2E: 0x75, // Delete → Forward Delete
        
        // Arrow keys
        0x25: 0x7B, // Left
        0x26: 0x7E, // Up
        0x27: 0x7C, // Right
        0x28: 0x7D, // Down
        
        // Modifiers
        0x10: 0x38, // Shift
        0xA0: 0x38, // LShift
        0xA1: 0x3C, // RShift
        0x11: 0x3B, // Ctrl → Control
        0xA2: 0x3B, // LCtrl
        0xA3: 0x3E, // RCtrl
        0x12: 0x3A, // Alt → Option
        0xA4: 0x3A, // LAlt
        0xA5: 0x3D, // RAlt
        0x5B: 0x37, // LWin → Command
        0x5C: 0x36, // RWin → RCommand
        0x14: 0x39, // CapsLock
        
        // Punctuation
        0xBA: 0x29, // ;: → Semicolon
        0xBB: 0x18, // =+ → Equal
        0xBC: 0x2B, // ,< → Comma
        0xBD: 0x1B, // -_ → Minus
        0xBE: 0x2F, // .> → Period
        0xBF: 0x2C, // /? → Slash
        0xC0: 0x32, // `~ → Grave
        0xDB: 0x21, // [{ → LeftBracket
        0xDC: 0x2A, // \| → Backslash
        0xDD: 0x1E, // ]} → RightBracket
        0xDE: 0x27, // '" → Quote
        
        // JIS-specific
        0xF2: 0x66, // 英数 (Eisu) → JIS Eisu
        0xF3: 0x68, // かな (Kana) → JIS Kana
        0x1C: 0x66, // Henkan → JIS Eisu
        0x1D: 0x68, // Muhenkan → JIS Kana
    ]
}
