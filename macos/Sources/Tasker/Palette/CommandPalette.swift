import AppKit
import SwiftUI
import TaskerAPI

final class PalettePanel: NSPanel {
    var onCancel: (() -> Void)?
    var onResign: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }

    override func resignKey() {
        super.resignKey()
        onResign?()
    }
}

@MainActor
final class CommandPalette {
    static let topOffset: CGFloat = 96

    private let layout: WorkspaceLayout
    private let sessions: WorkspaceSessions
    private let workspaces: WorkspaceController
    private let connection: DaemonConnection
    private let defaults: UserDefaults
    private var panel: PalettePanel?
    private var model: PaletteModel?
    private var hosting: NSHostingView<PaletteView>?
    private var anchor: NSWindow?
    private var resizing: Task<Void, Never>?

    init(layout: WorkspaceLayout, sessions: WorkspaceSessions, workspaces: WorkspaceController, connection: DaemonConnection, defaults: UserDefaults = .standard) {
        self.layout = layout
        self.sessions = sessions
        self.workspaces = workspaces
        self.connection = connection
        self.defaults = defaults
    }

    var isShown: Bool { panel?.isVisible == true }

    func toggle(window id: UUID?) {
        if isShown { close() } else { present(window: id) }
    }

    func present(window id: UUID?) {
        close()
        let window = id.flatMap { layout.window($0) }
        let tab = window?.selectedTab
        let session = tab.map { sessions.session(for: $0) }
        let l = Localizer(choice: AppLanguage(storedValue: defaults.string(forKey: AppLanguage.storageKey) ?? ""))
        let source = session?.live.client.map { GraphQLPaletteSource(client: $0) }
        let project = session?.selection.current?.id
        let windowId = window?.id

        let model = PaletteModel(
            localizer: l,
            source: source,
            project: project,
            makeCommands: { [weak self] vocabulary in
                guard let self else { return [] }
                return PaletteCommands.build(
                    self.context(session: session, tab: tab), vocabulary: vocabulary,
                    actions: self.actions(session: session, window: windowId, l: l), l: l)
            },
            openTask: { [weak self] task in
                guard let session else { return }
                session.setSection(.tasks)
                session.navigator.open(task)
                self?.refocus()
            },
            close: { [weak self] in self?.close() })
        self.model = model

        let view = PaletteView(model: model) { [weak self] in self?.close() }
        let hosting = NSHostingView(rootView: view)
        hosting.sizingOptions = []
        self.hosting = hosting
        let size = NSSize(width: PaletteMetrics.width, height: PaletteMetrics.height(model.sections))
        let panel = PalettePanel(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.contentView = hosting
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.hasShadow = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hidesOnDeactivate = true
        panel.isReleasedWhenClosed = false
        panel.becomesKeyOnlyIfNeeded = false
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        panel.onCancel = { [weak self] in self?.close() }
        panel.onResign = { [weak self] in
            DispatchQueue.main.async { self?.close() }
        }
        anchor = NSApp.keyWindow ?? NSApp.mainWindow
        self.panel = panel
        position(panel, size: size)
        panel.makeKeyAndOrderFront(nil)
        model.start()
        track(model)
    }

    func close() {
        resizing?.cancel()
        resizing = nil
        model?.stop()
        model = nil
        let panel = self.panel
        self.panel = nil
        hosting = nil
        panel?.onResign = nil
        panel?.onCancel = nil
        panel?.orderOut(nil)
        panel?.contentView = nil
        if let anchor, anchor.isVisible { anchor.makeKeyAndOrderFront(nil) }
        anchor = nil
    }

    private func refocus() {
        anchor?.makeKeyAndOrderFront(nil)
    }

    private func track(_ model: PaletteModel) {
        withObservationTracking {
            _ = model.sections
        } onChange: { [weak self, weak model] in
            Task { @MainActor in
                guard let self, let model, self.model === model, let panel = self.panel else { return }
                self.position(panel, size: NSSize(width: PaletteMetrics.width, height: PaletteMetrics.height(model.sections)))
                self.track(model)
            }
        }
    }

    private func position(_ panel: NSPanel, size: NSSize) {
        let reference = anchor?.frame ?? NSScreen.main?.visibleFrame ?? .zero
        let top = reference.maxY - Self.topOffset
        let x = reference.midX - size.width / 2
        panel.setFrame(NSRect(x: x, y: top - size.height, width: size.width, height: size.height), display: true)
    }

    private func context(session: WorkspaceTabSession?, tab: WorkspaceTab?) -> PaletteContext {
        let status = connection.state.status
        let open = layout.openPaths
        var paths = open
        for path in workspaces.reopenable(in: status) where !paths.contains(path) { paths.append(path) }
        let current = tab?.path
        return PaletteContext(
            hasTab: session != nil,
            hasProject: session?.selection.current != nil && session?.live.client != nil,
            projects: session?.live.projects ?? [],
            currentProject: session?.selection.current?.id,
            workspaces: paths.map { path in PaletteWorkspace(path: path, current: current.map { WorkspacePath.same($0, path) } ?? false) },
            theme: AppTheme.current(defaults: defaults),
            isDark: AppTheme.effectiveIsDark,
            language: AppLanguage(storedValue: defaults.string(forKey: AppLanguage.storageKey) ?? ""))
    }

    private func actions(session: WorkspaceTabSession?, window: UUID?, l: Localizer) -> PaletteActions {
        PaletteActions(
            goTo: { section in session?.setSection(section) },
            createTask: {
                session?.setSection(.tasks)
                session?.creation.present()
            },
            createBoard: { [sessions] in
                guard let session, let project = session.selection.current?.id,
                      sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: nil), l) else { return }
                session.openBoardEditor(project: project, board: nil, localizer: l)
            },
            editBoard: { [sessions] id in
                guard let session, let project = session.selection.current?.id,
                      sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: id), l) else { return }
                session.openBoardEditor(project: project, board: id, localizer: l)
            },
            showSeries: { id in session?.showSeries(id) },
            chooseProject: { id in session?.selection.choose(id) },
            openWorkspace: { [layout, workspaces] path in
                guard let window else { return }
                if layout.openPaths.contains(WorkspacePath.canonical(path)) {
                    layout.open(path, in: window)
                } else {
                    Task { await workspaces.reopen(path, window: window) }
                }
            },
            openFolder: { [workspaces] in
                guard let window else { return }
                Task { await workspaces.openFolder(l, window: window) }
            },
            setTheme: { [defaults] theme in AppTheme.set(theme, defaults: defaults) },
            setLanguage: { [defaults] option in defaults.set(option.rawValue, forKey: AppLanguage.storageKey) })
    }
}

extension EnvironmentValues {
    @Entry var commandPalette: CommandPalette?
}
