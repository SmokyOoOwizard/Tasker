import AppKit
import SwiftUI

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

    private static let significant: NSEvent.ModifierFlags = [.command, .control, .option, .shift]

    static func menuItem(in menu: NSMenu, key: Character, modifiers: NSEvent.ModifierFlags) -> (menu: NSMenu, index: Int)? {
        let wanted = modifiers.intersection(significant)
        let text = String(key)
        for (index, item) in menu.items.enumerated() {
            if let submenu = item.submenu, let found = menuItem(in: submenu, key: key, modifiers: modifiers) { return found }
            guard !item.keyEquivalent.isEmpty, item.keyEquivalent.lowercased() == text.lowercased() else { continue }
            var mask = item.keyEquivalentModifierMask.intersection(significant)
            if item.keyEquivalent != item.keyEquivalent.lowercased() { mask.insert(.shift) }
            if mask == wanted || mask.subtracting(.shift) == wanted.subtracting(.shift) && !text.unicodeScalars.contains(where: CharacterSet.letters.contains) {
                return (menu, index)
            }
        }
        return nil
    }

    private static func flags(_ modifiers: EventModifiers) -> NSEvent.ModifierFlags {
        var result: NSEvent.ModifierFlags = []
        if modifiers.contains(.command) { result.insert(.command) }
        if modifiers.contains(.control) { result.insert(.control) }
        if modifiers.contains(.option) { result.insert(.option) }
        if modifiers.contains(.shift) { result.insert(.shift) }
        return result
    }

    static func command(key: Character, modifiers: NSEvent.ModifierFlags) -> AppCommand? {
        let wanted = modifiers.intersection(significant)
        let isLetter = key.unicodeScalars.contains(where: CharacterSet.letters.contains)
        return AppCommand.shortcutBearing.first { command in
            guard let shortcut = command.shortcut, shortcut.key == KeyEquivalent(Character(String(key).lowercased())) else { return false }
            let mask = flags(shortcut.modifiers)
            return mask == wanted || (!isLetter && mask.subtracting(.shift) == wanted.subtracting(.shift))
        }
    }

    @MainActor static var dispatch: ((AppCommand, UUID?) -> Void)?

    @MainActor static func perform(_ event: NSEvent) -> Bool {
        guard event.type == .keyDown,
              !event.modifierFlags.isDisjoint(with: [.command, .control]),
              needsTranslation(event.charactersIgnoringModifiers),
              let key = character(keyCode: event.keyCode, shift: event.modifierFlags.contains(.shift)) else { return false }
        if let main = NSApp.mainMenu, let found = menuItem(in: main, key: key, modifiers: event.modifierFlags),
           found.menu.items[found.index].isEnabled {
            found.menu.performActionForItem(at: found.index)
            return true
        }
        if let command = command(key: key, modifiers: event.modifierFlags), let dispatch {
            dispatch(command, WorkspaceWindowRegistry.id(of: NSApp.keyWindow))
            return true
        }
        return false
    }

    @MainActor private static var monitor: Any?

    @MainActor static func install() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            nonisolated(unsafe) let original = event
            let handled = MainActor.assumeIsolated { perform(original) }
            return handled ? nil : original
        }
    }
}
