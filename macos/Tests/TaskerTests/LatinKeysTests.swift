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

    @Test func translatedEventKeepsKeyCodeAndUsesLatinLetter() throws {
        let event = try #require(NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0, windowNumber: 0,
            context: nil, characters: "л", charactersIgnoringModifiers: "л", isARepeat: false, keyCode: 40))
        let latin = try #require(LatinKeys.translated(event))
        #expect(latin.charactersIgnoringModifiers == "k")
        #expect(latin.keyCode == 40)
        #expect(latin.modifierFlags.contains(.command))
    }

    @Test func plainLatinEventIsNotTranslated() throws {
        let event = try #require(NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0, windowNumber: 0,
            context: nil, characters: "k", charactersIgnoringModifiers: "k", isARepeat: false, keyCode: 40))
        #expect(LatinKeys.translated(event) == nil)
    }
}
