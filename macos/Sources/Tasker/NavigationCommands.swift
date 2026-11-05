import AppKit
import SwiftUI

enum SidebarState {
    static let storageKey = "sidebarVisible"
}

struct ActiveWorkspaceWindow: Equatable {
    struct Target: Equatable, Identifiable {
        let id: UUID
        let title: String
    }

    let id: UUID
    let tab: UUID?
    let sidebarVisible: Bool
    let commands: CommandState
    let others: [Target]
}

struct ActiveWorkspaceWindowKey: FocusedValueKey {
    typealias Value = ActiveWorkspaceWindow
}

extension FocusedValues {
    var activeWorkspaceWindow: ActiveWorkspaceWindow? {
        get { self[ActiveWorkspaceWindowKey.self] }
        set { self[ActiveWorkspaceWindowKey.self] = newValue }
    }
}

struct NavigationCommands: Commands {
    let runner: CommandRunner
    @FocusedValue(\.activeWorkspaceWindow) private var active
    @Environment(\.openWindow) private var openWindow
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue
    @AppStorage(AppTheme.storageKey) private var storedTheme = AppTheme.system.rawValue

    private var l: Localizer {
        Localizer(choice: AppLanguage(storedValue: storedLanguage))
    }

    private var state: CommandState {
        active?.commands ?? .none
    }

    private func run(_ command: AppCommand) {
        runner.openWindow = { openWindow(value: $0) }
        runner.openShortcuts = { openWindow(id: ShortcutsWindow.id) }
        runner.perform(command, window: active?.id)
    }

    private func item(_ command: AppCommand, title: String? = nil) -> some View {
        Button(title ?? l.string(command.titleKey)) { run(command) }
            .disabled(!command.isEnabled(state))
            .optionalShortcut(command.shortcut)
    }

    private func modeItem(_ mode: TaskListMode) -> some View {
        let command = AppCommand.listMode(mode)
        return Toggle(l.string(mode.titleKey), isOn: Binding(get: { state.listMode == mode }, set: { _ in run(command) }))
            .disabled(!command.isEnabled(state))
            .optionalShortcut(command.shortcut)
    }

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button(l.string(.menuAbout, l.string(.appName))) {
                showAbout()
            }
        }
        CommandGroup(replacing: .newItem) {
            item(.newTask)
            item(.newBoard)
            Divider()
            item(.newTab)
            item(.newWindow)
            Divider()
            item(.openFolder)
        }
        CommandGroup(replacing: .saveItem) {
            item(.closeTab)
            item(.closeWindow)
        }
        CommandGroup(replacing: .sidebar) {
            item(.palette)
            item(.toggleSidebar, title: (active?.sidebarVisible ?? true) ? l.string(.sidebarHide) : l.string(.sidebarShow))
            Divider()
            ForEach(AppSection.allCases) { section in
                item(.go(section))
            }
            Divider()
            Menu(l.string(.menuListLayout)) {
                ForEach(TaskListMode.allCases) { modeItem($0) }
            }
            Menu(l.string(.menuTheme)) {
                Picker(l.string(.menuTheme), selection: Binding(
                    get: { AppTheme(storedValue: storedTheme) },
                    set: { run(.theme($0)) }
                )) {
                    ForEach(AppTheme.allCases, id: \.self) { Text(l.string($0.titleKey)).tag($0) }
                }
                .pickerStyle(.inline)
                .labelsHidden()
            }
        }
        CommandMenu(l.string(.menuTask)) {
            item(.taskStatus)
            item(.taskLink)
            item(.taskParent)
            Divider()
            item(.taskCopyKey)
        }
        CommandGroup(after: .windowArrangement) {
            Divider()
            item(.previousTab)
            item(.nextTab)
            Divider()
            item(.moveTabToNewWindow)
            Menu(l.string(.tabMoveToWindow)) {
                ForEach(active?.others ?? []) { other in
                    Button(other.title) { runner.moveTab(in: active?.id, to: other.id) }
                }
            }
            .disabled(active?.tab == nil || (active?.others ?? []).isEmpty)
            item(.mergeWindows)
        }
        CommandGroup(replacing: .help) {
            item(.help)
            item(.shortcuts)
        }
    }

    private func showAbout() {
        let credits = NSAttributedString(string: l.string(.appCredits))
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationName: l.string(.appName),
            .applicationVersion: AppInfo.version,
            .credits: credits
        ])
    }
}

extension View {
    @ViewBuilder
    func optionalShortcut(_ shortcut: CommandShortcut?) -> some View {
        if let shortcut {
            keyboardShortcut(shortcut.key, modifiers: shortcut.modifiers)
        } else {
            self
        }
    }
}
