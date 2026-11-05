import AppKit

enum LatinKeys {
    private static let base: [UInt16: Character] = [
        0: "a", 1: "s", 2: "d", 3: "f", 4: "h", 5: "g", 6: "z", 7: "x", 8: "c", 9: "v", 11: "b", 12: "q", 13: "w", 14: "e",
        15: "r", 16: "y", 17: "t", 18: "1", 19: "2", 20: "3", 21: "4", 22: "6", 23: "5", 24: "=", 25: "9", 26: "7", 27: "-",
        28: "8", 29: "0", 30: "]", 31: "o", 32: "u", 33: "[", 34: "i", 35: "p", 37: "l", 38: "j", 39: "'", 40: "k", 41: ";",
        42: "\\", 43: ",", 44: "/", 45: "n", 46: "m", 47: ".", 50: "`"
    ]

    private static let shifted: [Character: Character] = [
        "=": "+", "-": "_", "]": "}", "[": "{", "'": "\"", ";": ":", "\\": "|", ",": "<", "/": "?", ".": ">", "`": "~"
    ]

    static func character(keyCode: UInt16, shift: Bool) -> Character? {
        guard let key = base[keyCode] else { return nil }
        return shift ? shifted[key] ?? key : key
    }

    static func needsTranslation(_ characters: String?) -> Bool {
        guard let scalar = characters?.unicodeScalars.first else { return false }
        return scalar.value > 127
    }

    static func character(for event: NSEvent?) -> String? {
        guard let event, event.type == .keyDown, needsTranslation(event.charactersIgnoringModifiers) else {
            return event?.charactersIgnoringModifiers
        }
        return character(keyCode: event.keyCode, shift: event.modifierFlags.contains(.shift)).map(String.init)
            ?? event.charactersIgnoringModifiers
    }

    static func translated(_ event: NSEvent) -> NSEvent? {
        guard event.type == .keyDown,
              !event.modifierFlags.isDisjoint(with: [.command, .control]),
              needsTranslation(event.charactersIgnoringModifiers),
              let key = character(keyCode: event.keyCode, shift: event.modifierFlags.contains(.shift)) else { return nil }
        let text = String(key)
        return NSEvent.keyEvent(
            with: .keyDown,
            location: event.locationInWindow,
            modifierFlags: event.modifierFlags,
            timestamp: event.timestamp,
            windowNumber: event.windowNumber,
            context: nil,
            characters: text,
            charactersIgnoringModifiers: text,
            isARepeat: event.isARepeat,
            keyCode: event.keyCode)
    }

    @MainActor private static var monitor: Any?

    @MainActor static func install() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            nonisolated(unsafe) let original = event
            let handled = MainActor.assumeIsolated {
                translated(original).map { NSApp.mainMenu?.performKeyEquivalent(with: $0) == true } ?? false
            }
            return handled ? nil : original
        }
    }
}
