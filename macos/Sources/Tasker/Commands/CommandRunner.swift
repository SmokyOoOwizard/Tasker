import AppKit
import SwiftUI

@MainActor
final class CommandRunner {
    private let layout: WorkspaceLayout
    private let sessions: WorkspaceSessions
    private let workspaces: WorkspaceController
    private let defaults: UserDefaults

    weak var palette: CommandPalette?
    let shortcuts = ShortcutsPresenter()
    var openWindow: (UUID) -> Void = { _ in }

    init(layout: WorkspaceLayout, sessions: WorkspaceSessions, workspaces: WorkspaceController, defaults: UserDefaults = .standard) {
        self.layout = layout
        self.sessions = sessions
        self.workspaces = workspaces
        self.defaults = defaults
    }

    private var localizer: Localizer {
        Localizer(choice: AppLanguage(storedValue: defaults.string(forKey: AppLanguage.storageKey) ?? ""))
    }

    private func session(in window: UUID?) -> WorkspaceTabSession? {
        window.flatMap { layout.window($0) }?.selectedTab.map(sessions.session(for:))
    }

    func state(window id: UUID?) -> CommandState {
        guard let id, let window = layout.window(id) else { return .none }
        let session = window.selectedTab.map(sessions.session(for:))
        let hasProject = session?.selection.current != nil && session?.live.client != nil
        return CommandState(
            hasWindow: true,
            hasTab: session != nil,
            hasProject: hasProject,
            hasTask: hasProject && session?.commandTask != nil,
            hasLink: hasProject && session?.currentLink() != nil,
            tabCount: window.tabs.count,
            otherWindows: layout.windows.count - 1,
            listMode: hasProject && session?.section == .tasks ? session?.currentListMode : nil)
    }

    func moveTab(in id: UUID?, to target: UUID) {
        if let tab = id.flatMap({ layout.window($0) })?.selected { layout.move(tab, to: target) }
    }

    func perform(_ command: AppCommand, window id: UUID?) {
        guard command.isEnabled(state(window: id)) else { return }
        let l = localizer
        let window = id.flatMap { layout.window($0) }
        let session = session(in: id)
        switch command {
        case .newTask:
            session?.setSection(.tasks)
            session?.creation.present()
        case .newBoard:
            guard let session, let project = session.selection.current?.id,
                  sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: nil), l) else { return }
            session.openBoardEditor(project: project, board: nil, localizer: l)
        case .newTab:
            if let tab = window?.selected { layout.duplicate(tab) }
        case .newWindow:
            openWindow(layout.createWindow())
        case .openFolder:
            guard let id else { return }
            Task { await workspaces.openFolder(l, window: id) }
        case .closeTab:
            if let tab = window?.selected {
                sessions.close(tab, l)
            } else {
                NSApp.keyWindow?.performClose(nil)
            }
        case .closeWindow:
            if let window, !sessions.confirmClosing(window: window.id, l) { return }
            NSApp.keyWindow?.close()
        case .palette:
            palette?.toggle(window: id)
        case .toggleSidebar:
            if let window { layout.setSidebar(!window.sidebarVisible, in: window.id) }
        case .go(let section):
            session?.setSection(section)
        case .listMode(let mode):
            session?.setSection(.tasks)
            session?.setListMode(mode)
        case .theme(let theme):
            AppTheme.set(theme, defaults: defaults)
        case .taskStatus:
            if let session { showStatusMenu(session, l) }
        case .taskLink:
            session?.requestLink(.link)
        case .taskParent:
            session?.requestLink(.parent)
        case .taskCopyKey:
            session?.copyTaskKey(l)
        case .copyLink:
            session?.copyLink(l)
        case .nextTab:
            if let window { layout.selectAdjacent(in: window.id, offset: 1) }
        case .previousTab:
            if let window { layout.selectAdjacent(in: window.id, offset: -1) }
        case .moveTabToNewWindow:
            if let tab = window?.selected, let target = layout.detach(tab) { openWindow(target) }
        case .mergeWindows:
            if let window { layout.mergeAll(into: window.id) }
        case .shortcuts:
            if let id { shortcuts.show(in: id) }
        case .help:
            NSApp.showHelp(nil)
        }
    }

    private func showStatusMenu(_ session: WorkspaceTabSession, _ l: Localizer) {
        guard let choices = session.statusChoices(), let view = NSApp.keyWindow?.contentView else { return }
        let menu = NSMenu()
        menu.autoenablesItems = false
        let header = NSMenuItem(title: l.string(.taskListStatusTitle, choices.reference), action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        for (index, status) in choices.options.enumerated() {
            let action = StatusMenuAction { choices.apply(status.id) }
            let item = NSMenuItem(title: status.name, action: #selector(StatusMenuAction.fire), keyEquivalent: index < 9 ? String(index + 1) : "")
            item.keyEquivalentModifierMask = []
            item.target = action
            item.representedObject = action
            item.state = status.id == choices.current ? .on : .off
            menu.addItem(item)
        }
        let point = NSPoint(x: view.bounds.midX - 120, y: view.isFlipped ? 140 : view.bounds.maxY - 140)
        menu.popUp(positioning: nil, at: point, in: view)
    }
}

private final class StatusMenuAction: NSObject {
    let run: @MainActor () -> Void

    init(_ run: @escaping @MainActor () -> Void) {
        self.run = run
    }

    @MainActor @objc func fire() {
        run()
    }
}

extension EnvironmentValues {
    @Entry var commandRunner: CommandRunner?
}
