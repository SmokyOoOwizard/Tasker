import AppKit
import SwiftUI
import Testing
@testable import Tasker

@MainActor
struct LatinKeysTests {
    @Test func cyrillicLetterKeyMapsToItsLatinKey() {
        #expect(LatinKeys.character(keyCode: 40, shift: false) == "k")
        #expect(LatinKeys.character(keyCode: 37, shift: false) == "l")
        #expect(LatinKeys.character(keyCode: 38, shift: false) == "j")
    }

    @Test func shiftedPunctuationUsesShiftedCharacter() {
        #expect(LatinKeys.character(keyCode: 44, shift: true) == "?")
        #expect(LatinKeys.character(keyCode: 44, shift: false) == "/")
        #expect(LatinKeys.character(keyCode: 18, shift: true) == "1")
    }

    @Test func onlyNonLatinCharactersNeedTranslation() {
        #expect(LatinKeys.needsTranslation("л"))
        #expect(!LatinKeys.needsTranslation("k"))
        #expect(!LatinKeys.needsTranslation("1"))
        #expect(!LatinKeys.needsTranslation(nil))
    }

    @Test func everyRegistryShortcutIsReachableFromSomeKey() {
        for command in AppCommand.shortcutBearing {
            guard let shortcut = command.shortcut else { continue }
            let reachable = (0..<128).contains { code in
                LatinKeys.character(keyCode: UInt16(code), shift: false).map { KeyEquivalent($0) == shortcut.key } == true
                    || LatinKeys.character(keyCode: UInt16(code), shift: true).map { KeyEquivalent($0) == shortcut.key } == true
            }
            let special = shortcut.key == .tab
            #expect(reachable || special, "\(command.id)")
        }
    }

    @Test func findsMenuItemByLatinKeyAndModifiers() {
        let menu = NSMenu()
        let top = NSMenuItem(title: "File", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        let palette = NSMenuItem(title: "Palette", action: nil, keyEquivalent: "k")
        palette.keyEquivalentModifierMask = .command
        let board = NSMenuItem(title: "Board", action: nil, keyEquivalent: "n")
        board.keyEquivalentModifierMask = [.command, .option]
        sub.addItem(palette)
        sub.addItem(board)
        top.submenu = sub
        menu.addItem(top)
        #expect(LatinKeys.menuItem(in: menu, key: "k", modifiers: .command)?.index == 0)
        #expect(LatinKeys.menuItem(in: menu, key: "n", modifiers: [.command, .option])?.index == 1)
        #expect(LatinKeys.menuItem(in: menu, key: "n", modifiers: .command) == nil)
        #expect(LatinKeys.menuItem(in: menu, key: "k", modifiers: [.command, .shift]) == nil)
    }

    @Test func everyRegistryShortcutIsFoundByItsOwnKeyAndModifiers() {
        for command in AppCommand.shortcutBearing {
            guard let shortcut = command.shortcut, shortcut.key != .tab else { continue }
            let character = shortcut.key.character
            var flags: NSEvent.ModifierFlags = []
            if shortcut.modifiers.contains(.command) { flags.insert(.command) }
            if shortcut.modifiers.contains(.option) { flags.insert(.option) }
            if shortcut.modifiers.contains(.shift) { flags.insert(.shift) }
            #expect(LatinKeys.command(key: character, modifiers: flags) == command, "\(command.id)")
        }
    }

    @Test func plainLatinEventIsNotHandled() throws {
        let event = try #require(NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0, windowNumber: 0,
            context: nil, characters: "k", charactersIgnoringModifiers: "k", isARepeat: false, keyCode: 40))
        #expect(!LatinKeys.perform(event))
    }
}
