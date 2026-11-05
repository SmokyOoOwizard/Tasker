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
    let tabCount: Int
    let sidebarVisible: Bool
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
    let layout: WorkspaceLayout
    let sessions: WorkspaceSessions
    @FocusedValue(\.activeWorkspaceWindow) private var active
    @Environment(\.openWindow) private var openWindow
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    private var l: Localizer {
        Localizer(choice: AppLanguage(storedValue: storedLanguage))
    }

    private var current: WorkspaceWindow? {
        guard let active else { return nil }
        return layout.state.active.flatMap { layout.window($0) } ?? layout.window(active.id)
    }

    private var session: WorkspaceTabSession? {
        current?.selectedTab.map(sessions.session(for:))
    }

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button(l.string(.menuAbout, l.string(.appName))) {
                showAbout()
            }
        }
        CommandGroup(replacing: .newItem) {
            Button(l.string(.taskCreateAction)) {
                session?.setSection(.tasks)
                session?.creation.present()
            }
            .keyboardShortcut("n", modifiers: .command)
            .disabled(session == nil)
            Button(l.string(.tabNew)) {
                if let tab = current?.selected { layout.duplicate(tab) }
            }
            .keyboardShortcut("t", modifiers: .command)
            .disabled(active?.tab == nil)
            Button(l.string(.windowNew)) {
                openWindow(value: layout.createWindow())
            }
            .keyboardShortcut("n", modifiers: [.command, .shift])
        }
        CommandGroup(replacing: .saveItem) {
            Button(l.string(.workspaceCloseTab)) {
                if let tab = current?.selected {
                    layout.close(tab)
                } else {
                    NSApp.keyWindow?.performClose(nil)
                }
            }
            .keyboardShortcut("w", modifiers: .command)
            Button(l.string(.windowClose)) {
                NSApp.keyWindow?.performClose(nil)
            }
            .keyboardShortcut("w", modifiers: [.command, .shift])
        }
        CommandGroup(replacing: .sidebar) {
            Button((active?.sidebarVisible ?? true) ? l.string(.sidebarHide) : l.string(.sidebarShow)) {
                if let current { layout.setSidebar(!current.sidebarVisible, in: current.id) }
            }
            .keyboardShortcut("0", modifiers: .command)
            .disabled(active == nil)
            Divider()
            ForEach(AppSection.allCases) { section in
                Button(l.string(section.titleKey)) {
                    session?.setSection(section)
                }
                .keyboardShortcut(KeyEquivalent(section.shortcutKey), modifiers: .command)
                .disabled(session == nil)
            }
        }
        CommandGroup(after: .windowArrangement) {
            Divider()
            Button(l.string(.tabPrevious)) {
                if let current { layout.selectAdjacent(in: current.id, offset: -1) }
            }
            .keyboardShortcut(.tab, modifiers: [.control, .shift])
            .disabled((active?.tabCount ?? 0) < 2)
            Button(l.string(.tabNext)) {
                if let current { layout.selectAdjacent(in: current.id, offset: 1) }
            }
            .keyboardShortcut(.tab, modifiers: .control)
            .disabled((active?.tabCount ?? 0) < 2)
            Divider()
            Button(l.string(.tabMoveToNewWindow)) {
                if let tab = current?.selected, let id = layout.detach(tab) { openWindow(value: id) }
            }
            .disabled((active?.tabCount ?? 0) < 2)
            Menu(l.string(.tabMoveToWindow)) {
                ForEach(active?.others ?? []) { other in
                    Button(other.title) {
                        if let tab = current?.selected { layout.move(tab, to: other.id) }
                    }
                }
            }
            .disabled(active?.tab == nil || (active?.others ?? []).isEmpty)
            Button(l.string(.windowMergeAll)) {
                if let current { layout.mergeAll(into: current.id) }
            }
            .disabled((active?.others ?? []).isEmpty)
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
