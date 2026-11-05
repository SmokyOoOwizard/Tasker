import AppKit
import Foundation
import Observation
import TaskerAPI

@MainActor
@Observable
final class WorkspaceTabSession: Identifiable {
    let id: UUID
    let path: String?
    let live: LiveWorkspace
    let navigator = TaskNavigator()
    let creation = TaskCreationCoordinator()
    let drafts = TaskDrafts()
    let toasts: ToastQueue
    let settings: TaskListSettings
    let selection: ProjectSelection

    private(set) var seriesPage: UUID?
    var seriesPrefix: String?

    private var idleSection = AppSection.default

    @ObservationIgnored private weak var layout: WorkspaceLayout?
    @ObservationIgnored private var restored: WorkspaceTabViewState
    @ObservationIgnored private var lastProject: UUID?
    @ObservationIgnored private var listModel: TaskListModel?
    @ObservationIgnored private var listClient: TaskerAPIClient?
    @ObservationIgnored private var listOwner: UUID?
    @ObservationIgnored private var pendingFilter: TaskListFilter?

    init(tab: WorkspaceTab?, layout: WorkspaceLayout, live: LiveWorkspace, defaults: UserDefaults) {
        let id = tab?.id ?? UUID()
        self.id = id
        path = tab?.path
        self.live = live
        self.layout = layout
        toasts = ToastQueue()
        settings = TaskListSettings(defaults: defaults, tab: id)
        selection = ProjectSelection(tab: id, layout: layout, live: live)
        restored = tab?.view ?? WorkspaceTabViewState()
        lastProject = tab?.project
        navigator.restore(peek: restored.peek, card: restored.card, history: restored.history)
        navigator.onChange = { [weak layout] in layout?.scheduleSave() }
    }

    var section: AppSection {
        guard path != nil else { return idleSection }
        return AppSection(storedValue: layout?.tab(id)?.section ?? AppSection.default.rawValue)
    }

    func setSection(_ section: AppSection) {
        if path == nil {
            idleSection = section
        } else {
            layout?.setSection(section, tab: id)
        }
    }

    func notice(_ text: String) {
        toasts.show(.notice(text))
    }

    func noteProject(_ project: UUID?) {
        guard let project else { return }
        if let lastProject, lastProject != project {
            navigator.reset()
            creation.dismiss()
            restored = WorkspaceTabViewState()
            listModel?.stop()
            listModel = nil
            listClient = nil
            listOwner = nil
            pendingFilter = nil
            seriesPage = nil
            seriesPrefix = nil
        }
        lastProject = project
    }

    func attachList(owner: UUID, project: UUID, client: TaskerAPIClient, localizer: Localizer) -> TaskListModel {
        if let listModel, listModel.project == project, listClient === client {
            listOwner = owner
            listModel.localizer = localizer
            applyPendingFilter(to: listModel)
            listModel.resume()
            return listModel
        }
        listModel?.stop()
        let model = TaskListModel(project: project, source: GraphQLTaskListSource(client: client), settings: settings)
        model.localizer = localizer
        model.selectedRow = restored.selectedRow
        model.scrollY = restored.scrollY
        model.onNotice = { [weak self] text in self?.notice(text) }
        model.onOpen = { [navigator] row in navigator.open(row.id) }
        model.onPeek = { [navigator] row in navigator.togglePeek(row.id) }
        model.onSelect = { [navigator] row in if navigator.peek != nil { navigator.showPeek(row.id) } }
        model.onEscape = { [navigator] in
            guard navigator.peek != nil else { return false }
            navigator.closePeek()
            return true
        }
        model.onViewState = { [weak layout] in layout?.scheduleSave() }
        listModel = model
        listClient = client
        listOwner = owner
        applyPendingFilter(to: model)
        model.start()
        return model
    }

    func showSeries(_ series: UUID) {
        if seriesPage != series { seriesPrefix = nil }
        seriesPage = series
        navigator.closeCard()
        navigator.closePeek()
        apply(TaskListFilter(seriesIds: [series]))
    }

    func closeSeries() {
        guard seriesPage != nil else { return }
        seriesPage = nil
        seriesPrefix = nil
        apply(.none)
    }

    private func apply(_ filter: TaskListFilter) {
        let visible = section == .tasks && listOwner != nil
        if visible, let listModel {
            pendingFilter = nil
            listModel.filter = filter
        } else {
            pendingFilter = filter
            setSection(.tasks)
        }
    }

    private func applyPendingFilter(to model: TaskListModel) {
        guard let filter = pendingFilter else { return }
        pendingFilter = nil
        model.filter = filter
    }

    func detachList(owner: UUID) {
        guard listOwner == owner else { return }
        listOwner = nil
        listModel?.stop()
    }

    var viewState: WorkspaceTabViewState {
        WorkspaceTabViewState(
            peek: navigator.peek,
            card: navigator.card,
            history: navigator.history,
            selectedRow: listModel?.selectedRow ?? restored.selectedRow,
            scrollY: listModel?.scrollY ?? restored.scrollY
        )
    }

    func discard() {
        listModel?.stop()
        listModel = nil
        settings.removeTabData()
    }
}

@MainActor
@Observable
final class WorkspaceSessions {
    @ObservationIgnored private let layout: WorkspaceLayout
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let makeLive: () -> LiveWorkspace
    @ObservationIgnored private var sessions: [UUID: WorkspaceTabSession] = [:]
    @ObservationIgnored private var lives: [String: LiveWorkspace] = [:]
    @ObservationIgnored let idle: WorkspaceTabSession

    init(layout: WorkspaceLayout, defaults: UserDefaults = .standard, makeLive: @escaping () -> LiveWorkspace = { LiveWorkspace() }) {
        self.layout = layout
        self.defaults = defaults
        self.makeLive = makeLive
        idle = WorkspaceTabSession(tab: nil, layout: layout, live: makeLive(), defaults: defaults)
        layout.viewSnapshot = { [weak self] id in self?.sessions[id]?.viewState }
        layout.onTabRemoved = { [weak self] id in self?.remove(id) }
    }

    func hasUnsaved(_ tab: UUID) -> Bool {
        sessions[tab]?.drafts.isEmpty == false
    }

    func unsavedTabs(in window: UUID) -> [String] {
        (layout.window(window)?.tabs ?? []).filter { hasUnsaved($0.id) }.map { WorkspacePath.name($0.path) }
    }

    func close(_ tab: UUID, _ l: Localizer) {
        if hasUnsaved(tab) {
            layout.select(tab)
            let name = layout.tab(tab).map { WorkspacePath.name($0.path) } ?? ""
            guard confirmDiscard(l, body: l.string(.tabUnsavedBody, name)) else { return }
        }
        layout.close(tab)
    }

    func confirmClosing(window: UUID, _ l: Localizer) -> Bool {
        let names = unsavedTabs(in: window)
        guard !names.isEmpty else { return true }
        return confirmDiscard(l, body: l.string(.tabUnsavedWindowBody, names.joined(separator: ", ")))
    }

    private func confirmDiscard(_ l: Localizer, body: String) -> Bool {
        let alert = NSAlert()
        alert.messageText = l.string(.tabUnsavedTitle)
        alert.informativeText = body
        alert.addButton(withTitle: l.string(.commonCancel))
        alert.addButton(withTitle: l.string(.tabUnsavedDiscard))
        return alert.runModal() == .alertSecondButtonReturn
    }

    func session(for tab: WorkspaceTab) -> WorkspaceTabSession {
        if let existing = sessions[tab.id] { return existing }
        let session = WorkspaceTabSession(tab: tab, layout: layout, live: live(for: tab.path), defaults: defaults)
        sessions[tab.id] = session
        return session
    }

    func live(for path: String) -> LiveWorkspace {
        if let existing = lives[path] { return existing }
        let value = makeLive()
        lives[path] = value
        return value
    }

    var livePaths: [String] { Array(lives.keys) }

    func sync(with state: DaemonConnectionState) {
        let open = layout.openPaths
        for path in lives.keys where !open.contains(path) {
            lives[path]?.stop()
            lives[path] = nil
        }
        for path in open {
            live(for: path).follow(state, selected: path)
        }
    }

    private func remove(_ id: UUID) {
        sessions[id]?.discard()
        sessions[id] = nil
    }
}
