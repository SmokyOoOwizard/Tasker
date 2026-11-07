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
    private(set) var boardEditor: BoardEditorModel?
    private(set) var boardModel: BoardViewModel?
    private(set) var listQuery: TaskQueryModel?
    private(set) var boardQuery: TaskQueryModel?
    private(set) var boardId: UUID?
    var settingsPage = SettingsPage.statuses
    var adminPage = AdminPage.problems
    var taskRequest: TaskRequest?
    private(set) var listSelection: UUID?

    @ObservationIgnored weak var cardModel: TaskDetailModel?
    @ObservationIgnored weak var peekModel: TaskDetailModel?

    private var idleSection = AppSection.default

    @ObservationIgnored private weak var layout: WorkspaceLayout?
    @ObservationIgnored private var restored: WorkspaceTabViewState
    @ObservationIgnored private var boardQueryOwner: String?
    @ObservationIgnored private var lastProject: UUID?
    @ObservationIgnored private(set) var listModel: TaskListModel?
    @ObservationIgnored private var listClient: TaskerAPIClient?
    @ObservationIgnored private var listOwner: UUID?
    @ObservationIgnored private var pendingFilter: TaskListFilter?
    @ObservationIgnored private var boardClient: TaskerAPIClient?
    @ObservationIgnored private var boardOwner: UUID?
    @ObservationIgnored private var settingsStore: SettingsModel?
    @ObservationIgnored private var settingsClient: TaskerAPIClient?
    @ObservationIgnored private var adminStore: AdminModel?
    @ObservationIgnored private var adminClient: TaskerAPIClient?

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
        boardId = restored.board
        lastProject = tab?.project
        navigator.restore(peek: restored.peek, card: restored.card, history: restored.history)
        listSelection = restored.selectedRow.flatMap { $0.split(separator: "/").last }.flatMap { UUID(uuidString: String($0)) }
        navigator.onChange = { [weak layout] in layout?.scheduleSave() }
    }

    var section: AppSection {
        guard path != nil else { return idleSection }
        return AppSection(storedValue: layout?.tab(id)?.section ?? AppSection.default.rawValue)
    }

    func setSection(_ section: AppSection) {
        if section != self.section { navigator.reset() }
        if path == nil {
            idleSection = section
        } else {
            layout?.setSection(section, tab: id)
        }
    }

    func notice(_ text: String) {
        toasts.show(.notice(text))
    }

    func failure(_ text: String) {
        toasts.show(.failure(text))
    }

    func noteProject(_ project: UUID?) {
        guard let project else { return }
        if let lastProject, lastProject != project {
            navigator.reset()
            creation.dismiss()
            listSelection = nil
            taskRequest = nil
            listQuery = nil
            boardQuery = nil
            boardQueryOwner = nil
            restored = WorkspaceTabViewState()
            listModel?.stop()
            listModel = nil
            listClient = nil
            listOwner = nil
            pendingFilter = nil
            seriesPage = nil
            seriesPrefix = nil
            closeBoardEditor()
            closeBoard()
            boardId = nil
            closeSettings()
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
        let query = TaskQueryModel(project: project, source: GraphQLTaskQuerySource(client: client), text: restored.listQuery ?? "")
        model.query = query.applied
        query.onApply = { [weak model] text, hasSort in
            model?.query = text
            model?.queryHasSort = hasSort
        }
        query.onTextChange = { [weak layout] in layout?.scheduleSave() }
        listQuery = query
        query.start()
        model.onNotice = { [weak self] text in self?.notice(text) }
        model.onFailure = { [weak self] text in self?.failure(text) }
        model.onOpen = { [navigator] row in navigator.open(row.id) }
        model.onPeek = { [navigator] row in navigator.togglePeek(row.id) }
        model.onSelect = { [weak self] row in
            self?.listSelection = row.id
            if self?.navigator.peek != nil { self?.navigator.showPeek(row.id) }
        }
        model.onDeselect = { [weak self] in self?.listSelection = nil }
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

    var currentBoard: UUID? { boardModel?.board?.id ?? boardId }

    func showBoard(_ id: UUID) {
        if id != currentBoard { navigator.reset() }
        boardId = id
        setSection(.boards)
    }

    func attachBoard(owner: UUID, project: UUID, client: TaskerAPIClient, localizer: Localizer) -> BoardViewModel {
        if let boardModel, boardModel.project == project, boardModel.requested == boardId, boardClient === client {
            boardOwner = owner
            boardModel.localizer = localizer
            boardModel.resume()
            return boardModel
        }
        boardModel?.stop()
        rememberBoardQuery()
        let model = BoardViewModel(project: project, board: boardId, source: GraphQLBoardViewSource(client: client))
        model.localizer = localizer
        let key = boardQueryKey
        let query = TaskQueryModel(project: project, source: GraphQLTaskQuerySource(client: client), text: restored.boardQueries?[key] ?? "")
        model.query = query.applied
        query.onApply = { [weak model] text, _ in model?.query = text }
        query.onTextChange = { [weak layout] in layout?.scheduleSave() }
        boardQuery = query
        boardQueryOwner = key
        query.start()
        model.onNotice = { [weak self] text in self?.notice(text) }
        model.onFailure = { [weak self] text in self?.failure(text) }
        boardModel = model
        boardClient = client
        boardOwner = owner
        model.start()
        return model
    }

    func detachBoard(owner: UUID) {
        guard boardOwner == owner else { return }
        boardOwner = nil
        boardModel?.stop()
    }

    private var boardQueryKey: String { boardId?.uuidString ?? "" }

    private func rememberBoardQuery() {
        guard let boardQuery, let boardQueryOwner else { return }
        var saved = restored.boardQueries ?? [:]
        saved[boardQueryOwner] = boardQuery.text.isEmpty ? nil : boardQuery.text
        restored.boardQueries = saved.isEmpty ? nil : saved
    }

    private func closeBoard() {
        rememberBoardQuery()
        boardQuery = nil
        boardQueryOwner = nil
        boardModel?.stop()
        boardModel = nil
        boardClient = nil
        boardOwner = nil
    }

    @discardableResult
    func openBoardEditor(project: UUID, board: UUID?, localizer: Localizer) -> Bool {
        guard let client = live.client else { return false }
        setSection(.boards)
        let target = BoardEditorTarget(project: project, board: board)
        if boardEditor?.target == target { return true }
        closeBoardEditor()
        let model = BoardEditorModel(target: target, source: GraphQLBoardEditorSource(client: client))
        model.localizer = localizer
        model.onNotice = { [weak self] text in self?.notice(text) }
        model.onCreated = { [weak self] in self?.closeBoardEditor() }
        boardEditor = model
        model.load()
        return true
    }

    func closeBoardEditor() {
        boardEditor?.release()
        boardEditor = nil
    }

    var settingsModel: SettingsModel? { settingsStore }

    func openSettings(_ page: SettingsPage? = nil) {
        if let page { settingsPage = page }
        setSection(.settings)
    }

    func attachSettings(project: UUID, client: TaskerAPIClient, localizer: Localizer) -> SettingsModel {
        if let settingsStore, settingsStore.project == project, settingsClient === client {
            return settingsStore
        }
        closeSettings()
        let model = SettingsModel(project: project, source: GraphQLSettingsSource(client: client))
        model.localizer = localizer
        model.onNotice = { [weak self] text in self?.notice(text) }
        model.onFailure = { [weak self] text in self?.failure(text) }
        model.onProjectDeleted = { [weak self] in self?.projectDeleted(client: client) }
        settingsStore = model
        settingsClient = client
        model.load()
        return model
    }

    var adminModel: AdminModel? { adminStore }

    func openAdmin(_ page: AdminPage? = nil) {
        if let page { adminPage = page }
        setSection(.admin)
    }

    func attachAdmin(client: TaskerAPIClient, localizer: Localizer) -> AdminModel {
        if let adminStore, adminClient === client {
            adminStore.localizer = localizer
            return adminStore
        }
        closeAdmin()
        let model = AdminModel(source: GraphQLAdminSource(client: client))
        model.localizer = localizer
        model.onNotice = { [weak self] text in self?.notice(text) }
        adminStore = model
        adminClient = client
        model.load()
        return model
    }

    func closeAdmin() {
        adminStore?.stop()
        adminStore = nil
        adminClient = nil
    }

    func closeSettings() {
        settingsStore?.release()
        settingsStore = nil
        settingsClient = nil
    }

    private func projectDeleted(client: TaskerAPIClient) {
        closeSettings()
        navigator.reset()
        Task { _ = try? await client.graphQL.fetch(ProjectsQuery(limit: 200)) }
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

    private var currentListQuery: String? {
        let text = listQuery?.text ?? restored.listQuery ?? ""
        return text.isEmpty ? nil : text
    }

    private var currentBoardQueries: [String: String]? {
        var saved = restored.boardQueries ?? [:]
        if let boardQuery, let boardQueryOwner {
            saved[boardQueryOwner] = boardQuery.text.isEmpty ? nil : boardQuery.text
        }
        return saved.isEmpty ? nil : saved
    }

    var viewState: WorkspaceTabViewState {
        WorkspaceTabViewState(
            peek: navigator.peek,
            card: navigator.card,
            history: navigator.history,
            selectedRow: listModel?.selectedRow ?? restored.selectedRow,
            scrollY: listModel?.scrollY ?? restored.scrollY,
            listQuery: currentListQuery,
            boardQueries: currentBoardQueries,
            board: boardId
        )
    }

    func discard() {
        closeBoardEditor()
        closeBoard()
        closeSettings()
        closeAdmin()
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
    @ObservationIgnored var onUpdate: ((String, LiveUpdate) -> Void)?

    init(layout: WorkspaceLayout, defaults: UserDefaults = .standard, makeLive: @escaping () -> LiveWorkspace = { LiveWorkspace() }) {
        self.layout = layout
        self.defaults = defaults
        self.makeLive = makeLive
        idle = WorkspaceTabSession(tab: nil, layout: layout, live: makeLive(), defaults: defaults)
        layout.viewSnapshot = { [weak self] id in self?.sessions[id]?.viewState }
        layout.onTabRemoved = { [weak self] id in self?.remove(id) }
    }

    func hasUnsaved(_ tab: UUID) -> Bool {
        guard let session = sessions[tab] else { return false }
        return !session.drafts.isEmpty || session.boardEditor?.isDirty == true
    }

    func releaseEditorLocks() -> Task<Void, Never>? {
        let editors = sessions.values.compactMap(\.boardEditor)
        let settings = sessions.values.compactMap(\.settingsModel)
        guard !editors.isEmpty || !settings.isEmpty else { return nil }
        return Task { @MainActor in
            for editor in editors { await editor.releaseNow() }
            for model in settings { await model.releaseNow() }
        }
    }

    func confirmDiscardingBoardEditor(in session: WorkspaceTabSession, replacing target: BoardEditorTarget? = nil, _ l: Localizer) -> Bool {
        guard let editor = session.boardEditor, editor.isDirty, editor.target != target else { return true }
        return confirmDiscard(l, body: l.string(.boardDiscardBody))
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
        value.onUpdate = { [weak self] update in self?.onUpdate?(path, update) }
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
