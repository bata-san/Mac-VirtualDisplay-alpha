// Mac-Win Bridge Companion: Input injection – converts Windows input events to macOS CGEvents.

import Foundation
import CoreGraphics

/// Injects mouse and keyboard events into macOS from the Windows host.
/// Uses CGEvent API for precise, low-latency input injection.
class InputInjector {
    
    // Windows VK code to macOS keycode mapping (common keys)
    private static let vkToMacKeycode: [Int: UInt16] = [
        // Letters A-Z
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
        
        // Numbers 0-9
        0x30: 0x1D, 0x31: 0x12, 0x32: 0x13, 0x33: 0x14, 0x34: 0x15,
        0x35: 0x17, 0x36: 0x16, 0x37: 0x1A, 0x38: 0x1C, 0x39: 0x19,
        
        // Special keys
        0x0D: 0x24, // Enter → Return
        0x1B: 0x35, // Escape
        0x09: 0x30, // Tab
        0x20: 0x31, // Space
        0x08: 0x33, // Backspace → Delete
        0x2E: 0x75, // Delete → Forward Delete
        
        // Arrow keys
        0x25: 0x7B, // Left
        0x26: 0x7E, // Up
        0x27: 0x7C, // Right
        0x28: 0x7D, // Down
        
        // Modifiers
        0x10: 0x38, // Shift → Left Shift
        0x11: 0x3B, // Ctrl → Left Control
        0x12: 0x37, // Alt → Command (mapped to Cmd for Mac convenience)
        0x5B: 0x3A, // Win → Option
        
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
    ]
    
    /// Move the mouse cursor to the specified screen position.
    func moveMouse(x: Int, y: Int) {
        let point = CGPoint(x: x, y: y)
        
        if let event = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved,
                                mouseCursorPosition: point, mouseButton: .left) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    /// Simulate a mouse button press/release.
    /// Action values match Windows MouseHookAction enum.
    func mouseButton(action: Int) {
        let cursorPos = CGEvent(source: nil)?.location ?? .zero
        
        let (eventType, button): (CGEventType, CGMouseButton) = {
            switch action {
            case 1: return (.leftMouseDown,  .left)   // LeftDown
            case 2: return (.leftMouseUp,    .left)   // LeftUp
            case 3: return (.rightMouseDown, .right)   // RightDown
            case 4: return (.rightMouseUp,   .right)   // RightUp
            case 5: return (.otherMouseDown, .center)  // MiddleDown
            case 6: return (.otherMouseUp,   .center)  // MiddleUp
            default: return (.leftMouseDown, .left)
            }
        }()
        
        if let event = CGEvent(mouseEventSource: nil, mouseType: eventType,
                                mouseCursorPosition: cursorPos, mouseButton: button) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    /// Simulate scroll wheel.
    func scroll(deltaX: Int, deltaY: Int) {
        if let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel,
                                wheelCount: 2,
                                wheel1: Int32(deltaY / 120),
                                wheel2: Int32(deltaX / 120),
                                wheel3: 0) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    /// Simulate key down.
    func keyDown(vkCode: Int) {
        guard let macKeycode = Self.vkToMacKeycode[vkCode] else {
            print("[InputInjector] Unknown VK code: \(vkCode)")
            return
        }
        
        if let event = CGEvent(keyboardEventSource: nil, virtualKey: macKeycode, keyDown: true) {
            event.post(tap: .cghidEventTap)
        }
    }
    
    /// Simulate key up.
    func keyUp(vkCode: Int) {
        guard let macKeycode = Self.vkToMacKeycode[vkCode] else { return }
        
        if let event = CGEvent(keyboardEventSource: nil, virtualKey: macKeycode, keyDown: false) {
            event.post(tap: .cghidEventTap)
        }
    }
}
