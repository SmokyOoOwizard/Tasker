import AppKit
import Foundation

@MainActor
final class LinkOpener {
    private let layout: WorkspaceLayout
    private let sessions: WorkspaceSessions
    private let workspaces: WorkspaceController
    private let connection: DaemonConnection
    private let runner: CommandRunner
    private let defaults: UserDefaults
    private let makeSource: (WorkspaceTabSession) -> (any LinkSource)?
    private var running = false
    private var queue: [TaskerLink] = []

    init(
        layout: WorkspaceLayout, sessions: WorkspaceSessions, workspaces: WorkspaceController, connection: DaemonConnection,
        runner: CommandRunner, defaults: UserDefaults = .standard,
        makeSource: @escaping (WorkspaceTabSession) -> (any LinkSource)? = { $0.live.client.map { GraphQLLinkSource(client: $0) } }
    ) {
        self.layout = layout
        self.sessions = sessions
        self.workspaces = workspaces
        self.connection = connection
        self.runner = runner
        self.defaults = defaults
        self.makeSource = makeSource
    }

    private var localizer: Localizer {
        Localizer(choice: AppLanguage(storedValue: defaults.string(forKey: AppLanguage.storageKey) ?? ""))
    }

    func open(_ urls: [URL]) {
        for url in urls {
            guard let link = TaskerLink.parse(url) else {
                LinkPrompts.invalid(localizer)
                continue
            }
            queue.append(link)
        }
        drain()
    }

    func open(_ link: TaskerLink) {
        queue.append(link)
        drain()
    }

    private func drain() {
        guard !running, !queue.isEmpty else { return }
        running = true
        Task { @MainActor in
            while !queue.isEmpty {
                await handle(queue.removeFirst())
            }
            running = false
        }
    }

    func knownPaths() -> [String] {
        var result: [String] = []
        for path in layout.openPaths + layout.recents + (connection.state.status?.workspaces.map(\.path) ?? []) {
            let canonical = WorkspacePath.canonical(path)
            if !result.contains(canonical) { result.append(canonical) }
        }
        return result
    }

    private func path(for link: TaskerLink) async -> String? {
        if let found = knownPaths().first(where: { WorkspaceLinkID.matches(link.workspace, path: $0) }) { return found }
        for _ in 0..<80 where connection.state.status == nil {
            try? await Task.sleep(for: .milliseconds(100))
        }
        return knownPaths().first { WorkspaceLinkID.matches(link.workspace, path: $0) }
    }

    private func handle(_ link: TaskerLink) async {
        let l = localizer
        NSApp.activate(ignoringOtherApps: true)
        guard let path = await path(for: link) else {
            if LinkPrompts.unknownWorkspace(l), let window = await anyWindow() {
                await workspaces.openFolder(l, window: window)
            }
            return
        }
        guard let tab = await tab(for: path) else { return }
        layout.select(tab)
        if let window = layout.window(containing: tab) {
            layout.setActive(window.id)
            WorkspaceWindowRegistry.front(window.id)
        }
        guard link.target != .workspace else { return }
        let session = sessions.session(for: layout.tab(tab) ?? WorkspaceTab(path: path))
        await navigate(link.target, in: session, l)
    }

    private func anyWindow() async -> UUID? {
        await ensureWindow()
        return layout.state.active.flatMap { layout.window($0) }?.id ?? layout.windows.first?.id
    }

    private func ensureWindow() async {
        for _ in 0..<30 where !hasVisibleWindow {
            try? await Task.sleep(for: .milliseconds(100))
        }
        if !hasVisibleWindow { runner.openWindow(layout.createWindow()) }
    }

    private var hasVisibleWindow: Bool {
        NSApp.windows.contains { $0.isVisible && $0.canBecomeMain }
    }

    private func tab(for path: String) async -> UUID? {
        let matching = layout.windows.flatMap { window in window.tabs.filter { $0.path == path }.map { (window.id, $0.id) } }
        if let preferred = matching.first(where: { $0.0 == layout.state.active }) ?? matching.first(where: { layout.window($0.0)?.selected == $0.1 }) ?? matching.first {
            return preferred.1
        }
        guard let window = await anyWindow() else { return nil }
        let inDaemon = connection.state.status?.workspaces.contains { WorkspacePath.same($0.path, path) } == true
        if inDaemon {
            return layout.newTab(path, in: window)
        }
        await workspaces.reopen(path, window: window)
        return layout.window(window)?.tabs.first { $0.path == path }?.id
    }

    private func navigate(_ target: TaskerLinkTarget, in session: WorkspaceTabSession, _ l: Localizer) async {
        guard await ready(session) else {
            session.failure(l.string(.linkUnavailable))
            return
        }
        guard let source = makeSource(session) else { return }
        let projects = (session.live.projects ?? []).map { LinkProject(id: $0.id, name: $0.name) }
        let prioritized = projects.sorted { ($0.id == session.selection.current?.id ? 0 : 1) < ($1.id == session.selection.current?.id ? 0 : 1) }
        switch await LinkResolver.resolve(target, projects: prioritized, source: source) {
        case .notFound:
            session.failure(l.string(.linkNotFound))
        case .failed(let message):
            session.failure(message)
        case .found(let candidates):
            let chosen = candidates.count == 1 ? candidates[0] : LinkPrompts.choose(candidates, l)
            guard let chosen else { return }
            await show(chosen, in: session, l)
        }
    }

    private func ready(_ session: WorkspaceTabSession) async -> Bool {
        for _ in 0..<200 {
            if session.live.client != nil, session.live.projects != nil { return true }
            try? await Task.sleep(for: .milliseconds(100))
        }
        return false
    }

    private func show(_ candidate: LinkCandidate, in session: WorkspaceTabSession, _ l: Localizer) async {
        if session.selection.current?.id != candidate.project.id {
            session.selection.choose(candidate.project.id)
            try? await Task.sleep(for: .milliseconds(400))
        }
        switch candidate.destination {
        case .task(let id):
            session.setSection(.tasks)
            session.navigator.open(id)
        case .series(let id):
            session.showSeries(id)
        case .board(let id):
            let target = BoardEditorTarget(project: candidate.project.id, board: id)
            guard sessions.confirmDiscardingBoardEditor(in: session, replacing: target, l) else { return }
            session.openBoardEditor(project: candidate.project.id, board: id, localizer: l)
        }
    }
}
