import Foundation
import Testing
@testable import Tasker

private func isolatedDefaults() -> UserDefaults {
    UserDefaults(suiteName: "tasker-command-tests-\(UUID().uuidString)")!
}

@MainActor
@Suite struct AppCommandTests {
    static let reserved: Set<String> = [
        "⌘Q", "⌘H", "⌥⌘H", "⌘M", "⌥⌘M", "⌘,", "⌘⇥", "⌘`", "⌃⌘F", "⌃⌘Q", "⇧⌘3", "⇧⌘4", "⇧⌘5", "⌥⌘⎋", "⌘␣", "⌃⌘␣",
        "⌘A", "⌘C", "⌘V", "⌘X", "⌘Z", "⇧⌘Z", "⌘F", "⌘P", "⌘S", "⌘R", "⌘B", "⌘I", "⌘U", "⌘D", "⌘E", "⌘G", "⌘J", "⌘Y"
    ]

    @Test func shortcutsAreDistinct() {
        let shown = AppCommand.shortcutBearing.compactMap { $0.shortcut?.display }
        #expect(shown.count == AppCommand.shortcutBearing.count)
        #expect(Set(shown).count == shown.count)
    }

    @Test func noShortcutTakesASystemOrStandardKey() {
        for command in AppCommand.shortcutBearing {
            let display = command.shortcut?.display ?? ""
            #expect(!Self.reserved.contains(display), "\(command.id) uses \(display)")
        }
    }

    @Test func everyShortcutHasACommandModifier() {
        for command in AppCommand.shortcutBearing {
            guard let shortcut = command.shortcut else { continue }
            #expect(shortcut.modifiers.contains(.command) || shortcut.modifiers.contains(.control), "\(command.id)")
        }
    }

    @Test func tableMatchesCommands() {
        let rows = ShortcutTable.groups.flatMap(\.rows)
        var seen: Set<String> = []
        for row in rows {
            guard let id = row.command else { continue }
            let command = AppCommand.shortcutBearing.first { $0.id == id }
            #expect(command != nil, "unknown command \(id)")
            #expect(command?.shortcut?.tokens == row.keys, "\(id)")
            seen.insert(id)
        }
        #expect(seen == Set(AppCommand.shortcutBearing.map(\.id)))
    }

    @Test func tableTextsExistInBothLanguages() {
        for language in Language.allCases {
            let l = Localizer(language: language)
            for group in ShortcutTable.groups {
                #expect(l.string(group.title) != group.title.rawValue)
                for row in group.rows { #expect(l.string(row.title) != row.title.rawValue) }
            }
            for command in AppCommand.shortcutBearing { #expect(l.string(command.titleKey) != command.titleKey.rawValue) }
        }
    }

    @Test func taskCommandsNeedATask() {
        var state = CommandState(hasWindow: true, hasTab: true, hasProject: true)
        for command in [AppCommand.taskStatus, .taskLink, .taskParent, .taskCopyKey] { #expect(!command.isEnabled(state)) }
        state.hasTask = true
        for command in [AppCommand.taskStatus, .taskLink, .taskParent, .taskCopyKey] { #expect(command.isEnabled(state)) }
    }

    @Test func emptyWindowKeepsOnlyGlobalCommands() {
        let state = CommandState(hasWindow: true)
        #expect(AppCommand.openFolder.isEnabled(state))
        #expect(AppCommand.newWindow.isEnabled(state))
        #expect(AppCommand.closeTab.isEnabled(state))
        #expect(!AppCommand.newTask.isEnabled(state))
        #expect(!AppCommand.newTab.isEnabled(state))
        #expect(!AppCommand.go(.tasks).isEnabled(state))
        #expect(!AppCommand.nextTab.isEnabled(state))
        #expect(!AppCommand.listMode(.tree).isEnabled(state))
    }

    @Test func tabMovesNeedTwoTabs() {
        #expect(!AppCommand.nextTab.isEnabled(CommandState(hasWindow: true, hasTab: true, tabCount: 1)))
        #expect(AppCommand.nextTab.isEnabled(CommandState(hasWindow: true, hasTab: true, tabCount: 2)))
        #expect(!AppCommand.mergeWindows.isEnabled(CommandState(hasWindow: true)))
        #expect(AppCommand.mergeWindows.isEnabled(CommandState(hasWindow: true, otherWindows: 1)))
    }

    @Test func paletteSharesShortcutsWithMenu() {
        let l = Localizer(language: .en)
        let context = PaletteContext(
            hasTab: true, hasProject: true, projects: [], currentProject: nil, workspaces: [], theme: .system, isDark: false, language: .en,
            commands: CommandState(hasWindow: true, hasTab: true, hasProject: true, hasTask: true, listMode: .groups))
        let actions = PaletteActions(
            goTo: { _ in }, createTask: {}, createBoard: {}, editBoard: { _ in }, showSeries: { _ in }, openSettings: { _ in }, chooseProject: { _ in },
            openWorkspace: { _ in }, openFolder: {}, setTheme: { _ in }, setLanguage: { _ in })
        let entries = PaletteCommands.build(context, vocabulary: TaskListVocabulary(), actions: actions, l: l)
        func shortcut(_ id: String) -> String? { entries.first { $0.id == id }?.shortcut }
        #expect(shortcut("create-task") == AppCommand.newTask.shortcut?.display)
        #expect(shortcut("create-board") == AppCommand.newBoard.shortcut?.display)
        #expect(shortcut("open-folder") == AppCommand.openFolder.shortcut?.display)
        #expect(shortcut("go:tasks") == AppCommand.go(.tasks).shortcut?.display)
        #expect(shortcut("task:taskStatus") == AppCommand.taskStatus.shortcut?.display)
        #expect(shortcut("task:taskCopyKey") == AppCommand.taskCopyKey.shortcut?.display)
        #expect(shortcut("listmode:tree") == AppCommand.listMode(.tree).shortcut?.display)
        #expect(entries.first { $0.id == "listmode:groups" }?.isEnabled == false)
        #expect(entries.first { $0.id == "shortcuts" } != nil)
    }

    @Test func paletteHidesTaskCommandsWithoutATask() {
        let l = Localizer(language: .en)
        let context = PaletteContext(
            hasTab: true, hasProject: true, projects: [], currentProject: nil, workspaces: [], theme: .system, isDark: false, language: .en,
            commands: CommandState(hasWindow: true, hasTab: true, hasProject: true))
        let actions = PaletteActions(
            goTo: { _ in }, createTask: {}, createBoard: {}, editBoard: { _ in }, showSeries: { _ in }, openSettings: { _ in }, chooseProject: { _ in },
            openWorkspace: { _ in }, openFolder: {}, setTheme: { _ in }, setLanguage: { _ in })
        let ids = PaletteCommands.build(context, vocabulary: TaskListVocabulary(), actions: actions, l: l).map(\.id)
        #expect(!ids.contains { $0.hasPrefix("task:") })
    }
}

@MainActor
@Suite struct StartupBehaviorTests {
    private func layout(_ defaults: UserDefaults) -> (WorkspaceLayout, UUID) {
        let layout = WorkspaceLayout(defaults: defaults)
        let first = layout.claimWindow(requested: nil)
        layout.open("/a", in: first)
        layout.open("/b", in: first)
        let second = layout.createWindow()
        layout.open("/c", in: second)
        layout.flush()
        return (layout, first)
    }

    @Test func storedValueFallsBackToRestore() {
        #expect(StartupBehavior(storedValue: "") == .restore)
        #expect(StartupBehavior(storedValue: "junk") == .restore)
        #expect(StartupBehavior(storedValue: "empty") == .empty)
        #expect(StartupBehavior(storedValue: "lastFolder") == .lastFolder)
    }

    @Test func restoreChangesNothing() {
        let defaults = isolatedDefaults()
        _ = layout(defaults)
        let reopened = WorkspaceLayout(defaults: defaults)
        let before = reopened.state
        #expect(reopened.applyStartup(.restore).isEmpty)
        #expect(reopened.state == before)
    }

    @Test func emptyLeavesOneWindowWithoutTabsAndKeepsRecents() {
        let defaults = isolatedDefaults()
        _ = layout(defaults)
        let reopened = WorkspaceLayout(defaults: defaults)
        let dropped = reopened.applyStartup(.empty)
        #expect(dropped.count == 3)
        #expect(reopened.windows.count == 1)
        #expect(reopened.windows[0].tabs.isEmpty)
        #expect(reopened.windows[0].selected == nil)
        #expect(Set(reopened.recents) == ["/a", "/b", "/c"])
        #expect(reopened.state.initialized)
    }

    @Test func lastFolderKeepsOnlyTheSelectedTabOfTheActiveWindow() {
        let defaults = isolatedDefaults()
        let (original, first) = layout(defaults)
        original.setActive(first)
        original.flush()
        let reopened = WorkspaceLayout(defaults: defaults)
        let dropped = reopened.applyStartup(.lastFolder)
        #expect(dropped.count == 2)
        #expect(reopened.windows.count == 1)
        #expect(reopened.windows[0].tabs.map(\.path) == ["/b"])
        #expect(reopened.windows[0].selectedTab?.path == "/b")
        #expect(Set(reopened.recents) == ["/a", "/c"])
    }

    @Test func freshInstallIsLeftForSeeding() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        #expect(layout.applyStartup(.empty).isEmpty)
        #expect(!layout.state.initialized)
    }

    @Test func choiceSurvivesRestart() {
        let defaults = isolatedDefaults()
        defaults.set(StartupBehavior.empty.rawValue, forKey: StartupBehavior.storageKey)
        #expect(StartupBehavior.current(defaults: defaults) == .empty)
    }
}
