import Foundation
import Observation
import TaskerAPI

struct LegacyWorkspaceTabsState: Codable, Equatable, Sendable {
    var initialized = false
    var open: [String] = []
    var selected: String?
    var recents: [String] = []
    var projects: [String: String] = [:]
}

struct WorkspaceTabViewState: Codable, Equatable, Sendable {
    var peek: UUID?
    var card: UUID?
    var history: [UUID] = []
    var selectedRow: String?
    var scrollY: Double = 0
    var listQuery: String?
    var boardQueries: [String: String]?
    var board: UUID?
}

struct WorkspaceTab: Codable, Equatable, Identifiable, Sendable {
    var id = UUID()
    var path: String
    var project: UUID?
    var section = AppSection.default.rawValue
    var view = WorkspaceTabViewState()
}

struct WorkspaceWindow: Codable, Equatable, Identifiable, Sendable {
    var id = UUID()
    var tabs: [WorkspaceTab] = []
    var selected: UUID?
    var sidebarVisible = true
    var frameName: String?
    var frame: CGRect?

    var selectedTab: WorkspaceTab? { tabs.first { $0.id == selected } }

    func title(fallback: String) -> String {
        selectedTab.map { WorkspacePath.name($0.path) } ?? fallback
    }
}

struct WorkspaceLayoutState: Codable, Equatable, Sendable {
    var initialized = false
    var windows: [WorkspaceWindow] = []
    var active: UUID?
    var recents: [String] = []
}

@MainActor
@Observable
final class WorkspaceLayout {
    static let storageKey = "workspaceLayout"
    static let legacyStorageKey = "workspaceTabs"
    static let legacyFrameName = "TaskerMainWindow"
    static let maxRecents = 8
    static let saveDelay: Duration = .milliseconds(400)

    private(set) var state: WorkspaceLayoutState
    private(set) var draggingTab: UUID?

    @ObservationIgnored var viewSnapshot: ((UUID) -> WorkspaceTabViewState?)?
    @ObservationIgnored var onTabRemoved: ((UUID) -> Void)?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let key: String
    @ObservationIgnored private var attached: Set<UUID> = []
    @ObservationIgnored private var pendingFrames: [UUID: CGRect] = [:]
    @ObservationIgnored private var liveFrames: [UUID: CGRect] = [:]
    @ObservationIgnored private var saveTask: Task<Void, Never>?
    @ObservationIgnored private var terminating = false

    init(defaults: UserDefaults = .standard, key: String = WorkspaceLayout.storageKey, legacyKey: String = WorkspaceLayout.legacyStorageKey) {
        self.defaults = defaults
        self.key = key
        if let data = defaults.data(forKey: key), let stored = try? JSONDecoder().decode(WorkspaceLayoutState.self, from: data) {
            state = stored
        } else if let data = defaults.data(forKey: legacyKey), let legacy = try? JSONDecoder().decode(LegacyWorkspaceTabsState.self, from: data) {
            state = Self.migrated(legacy, defaults: defaults)
        } else {
            state = WorkspaceLayoutState()
        }
    }

    @discardableResult
    func applyStartup(_ behavior: StartupBehavior) -> [UUID] {
        guard behavior != .restore, state.initialized, !state.windows.isEmpty else { return [] }
        let keep = state.windows.first { $0.id == state.active } ?? state.windows[0]
        let kept: [WorkspaceTab] = behavior == .lastFolder ? keep.selectedTab.map { [$0] } ?? [] : []
        let keptIds = Set(kept.map(\.id))
        let dropped = state.windows.flatMap(\.tabs).filter { !keptIds.contains($0.id) }
        var window = keep
        window.tabs = kept
        window.selected = kept.first?.id
        state.windows = [window]
        state.active = window.id
        for tab in dropped.reversed() { remember(tab.path) }
        if let tab = kept.first { remember(tab.path) }
        save()
        return dropped.map(\.id)
    }

    var windows: [WorkspaceWindow] { state.windows }

    var recents: [String] {
        let open = Set(openPaths)
        return state.recents.filter { !open.contains($0) }
    }

    var openPaths: [String] {
        Self.unique(state.windows.flatMap { $0.tabs.map(\.path) })
    }

    func window(_ id: UUID) -> WorkspaceWindow? {
        state.windows.first { $0.id == id }
    }

    func tab(_ id: UUID) -> WorkspaceTab? {
        for window in state.windows {
            if let tab = window.tabs.first(where: { $0.id == id }) { return tab }
        }
        return nil
    }

    func window(containing tab: UUID) -> WorkspaceWindow? {
        state.windows.first { $0.tabs.contains { $0.id == tab } }
    }

    func claimWindow(requested: UUID?) -> UUID {
        let id: UUID
        if let requested {
            if window(requested) == nil { state.windows.append(WorkspaceWindow(id: requested, sidebarVisible: defaultSidebar)) }
            id = requested
        } else if let free = freeWindows().first {
            id = free
        } else {
            id = makeWindow()
        }
        attached.insert(id)
        save()
        return id
    }

    func takePendingWindows() -> [UUID] {
        let pending = freeWindows()
        attached.formUnion(pending)
        return pending
    }

    @discardableResult
    func createWindow() -> UUID {
        let id = freeWindows().first ?? makeWindow()
        attached.insert(id)
        save()
        return id
    }

    func windowClosed(_ id: UUID) {
        guard !terminating else { return }
        attached.remove(id)
        guard window(id) != nil else { return }
        if attached.isEmpty {
            closeTabs(in: id)
        } else {
            removeWindow(id)
        }
        save()
    }

    func prepareToTerminate() {
        terminating = true
        flush()
    }

    func setActive(_ id: UUID) {
        guard state.active != id, !terminating else { return }
        state.active = id
        scheduleSave()
    }

    func setSidebar(_ visible: Bool, in id: UUID) {
        guard let index = index(ofWindow: id), state.windows[index].sidebarVisible != visible else { return }
        state.windows[index].sidebarVisible = visible
        defaults.set(visible, forKey: SidebarState.storageKey)
        save()
    }

    func takePendingFrame(_ id: UUID) -> CGRect? {
        pendingFrames.removeValue(forKey: id)
    }

    func noteFrame(_ frame: CGRect, of id: UUID) {
        guard !terminating, liveFrames[id] != frame else { return }
        liveFrames[id] = frame
        scheduleSave()
    }

    func seed(from daemonPaths: [String]) {
        guard !state.initialized else { return }
        state.initialized = true
        let tabs = Self.unique(daemonPaths.map(WorkspacePath.canonical)).map { WorkspaceTab(path: $0) }
        if state.windows.isEmpty { _ = makeWindow() }
        state.windows[0].tabs = tabs
        state.windows[0].selected = tabs.first?.id
        save()
    }

    func open(_ path: String, in id: UUID) {
        let path = WorkspacePath.canonical(path)
        if let existing = window(id)?.tabs.first(where: { $0.path == path }) {
            select(existing.id)
            markOpened(path)
            return
        }
        addTab(WorkspaceTab(path: path), in: id)
        markOpened(path)
    }

    @discardableResult
    func newTab(_ path: String, in id: UUID) -> UUID? {
        let path = WorkspacePath.canonical(path)
        guard window(id) != nil else { return nil }
        let tab = WorkspaceTab(path: path)
        addTab(tab, in: id)
        markOpened(path)
        return tab.id
    }

    @discardableResult
    func duplicate(_ tab: UUID) -> UUID? {
        guard let source = self.tab(tab), let window = window(containing: tab) else { return nil }
        let copy = WorkspaceTab(path: source.path, project: source.project, section: source.section)
        addTab(copy, in: window.id)
        return copy.id
    }

    func close(_ tab: UUID) {
        guard let windowIndex = index(ofWindowContaining: tab), let index = state.windows[windowIndex].tabs.firstIndex(where: { $0.id == tab }) else { return }
        let removed = state.windows[windowIndex].tabs[index]
        takeOut(windowIndex: windowIndex, tabIndex: index)
        remember(removed.path)
        onTabRemoved?(tab)
        collapseEmpty(state.windows[windowIndex].id)
        save()
    }

    func select(_ tab: UUID) {
        guard let index = index(ofWindowContaining: tab), state.windows[index].selected != tab else { return }
        state.windows[index].selected = tab
        save()
    }

    func selectAdjacent(in id: UUID, offset: Int) {
        guard let window = window(id), !window.tabs.isEmpty, let selected = window.selected, let index = window.tabs.firstIndex(where: { $0.id == selected }) else { return }
        let count = window.tabs.count
        select(window.tabs[((index + offset) % count + count) % count].id)
    }

    func move(_ tab: UUID, to target: UUID, at position: Int? = nil) {
        guard let sourceIndex = index(ofWindowContaining: tab), let targetIndex = index(ofWindow: target),
              let tabIndex = state.windows[sourceIndex].tabs.firstIndex(where: { $0.id == tab }) else { return }
        let source = state.windows[sourceIndex].id
        let moving = state.windows[sourceIndex].tabs[tabIndex]
        var insertion = position ?? state.windows[targetIndex].tabs.count
        if source == target, tabIndex < insertion { insertion -= 1 }
        takeOut(windowIndex: sourceIndex, tabIndex: tabIndex)
        insertion = min(max(insertion, 0), state.windows[targetIndex].tabs.count)
        state.windows[targetIndex].tabs.insert(moving, at: insertion)
        state.windows[targetIndex].selected = tab
        if source != target { collapseEmpty(source) }
        save()
    }

    func detach(_ tab: UUID, frame: CGRect? = nil) -> UUID? {
        guard let source = window(containing: tab), source.tabs.count > 1 else { return nil }
        let id = makeWindow()
        if let frame { pendingFrames[id] = frame }
        move(tab, to: id)
        return id
    }

    func mergeAll(into target: UUID) {
        guard let targetIndex = index(ofWindow: target) else { return }
        for other in state.windows where other.id != target {
            state.windows[targetIndex].tabs.append(contentsOf: other.tabs)
        }
        for other in state.windows where other.id != target {
            attached.remove(other.id)
        }
        state.windows.removeAll { $0.id != target }
        if state.windows[0].selected == nil { state.windows[0].selected = state.windows[0].tabs.first?.id }
        save()
    }

    func setProject(_ id: UUID?, tab: UUID) {
        mutateTab(tab) { $0.project = id }
    }

    func setSection(_ section: AppSection, tab: UUID) {
        mutateTab(tab) { $0.section = section.rawValue }
    }

    func forget(recent path: String) {
        state.recents.removeAll { $0 == path }
        save()
    }

    func beginDragging(_ tab: UUID) {
        draggingTab = tab
    }

    func endDragging() {
        draggingTab = nil
    }

    func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: Self.saveDelay)
            guard !Task.isCancelled else { return }
            self?.save()
        }
    }

    func flush() {
        saveTask?.cancel()
        save()
    }

    private var defaultSidebar: Bool {
        state.windows.first { $0.id == state.active }?.sidebarVisible ?? defaults.object(forKey: SidebarState.storageKey) as? Bool ?? true
    }

    private func makeWindow() -> UUID {
        let window = WorkspaceWindow(sidebarVisible: defaultSidebar)
        state.windows.append(window)
        return window.id
    }

    private func freeWindows() -> [UUID] {
        let free = state.windows.map(\.id).filter { !attached.contains($0) }
        return free.filter { $0 != state.active } + free.filter { $0 == state.active }
    }

    private func index(ofWindow id: UUID) -> Int? {
        state.windows.firstIndex { $0.id == id }
    }

    private func index(ofWindowContaining tab: UUID) -> Int? {
        state.windows.firstIndex { $0.tabs.contains { $0.id == tab } }
    }

    private func addTab(_ tab: WorkspaceTab, in id: UUID) {
        guard let index = index(ofWindow: id) else { return }
        let position = state.windows[index].tabs.firstIndex { $0.id == state.windows[index].selected }.map { $0 + 1 } ?? state.windows[index].tabs.count
        state.windows[index].tabs.insert(tab, at: position)
        state.windows[index].selected = tab.id
        save()
    }

    private func markOpened(_ path: String) {
        state.initialized = true
        remember(path)
        save()
    }

    private func takeOut(windowIndex: Int, tabIndex: Int) {
        let id = state.windows[windowIndex].tabs[tabIndex].id
        state.windows[windowIndex].tabs.remove(at: tabIndex)
        if state.windows[windowIndex].selected == id {
            let tabs = state.windows[windowIndex].tabs
            state.windows[windowIndex].selected = tabs.isEmpty ? nil : tabs[min(tabIndex, tabs.count - 1)].id
        }
    }

    private func collapseEmpty(_ id: UUID) {
        guard state.windows.count > 1, window(id)?.tabs.isEmpty == true else { return }
        attached.remove(id)
        state.windows.removeAll { $0.id == id }
    }

    private func closeTabs(in id: UUID) {
        guard let index = index(ofWindow: id) else { return }
        for tab in state.windows[index].tabs {
            remember(tab.path)
            onTabRemoved?(tab.id)
        }
        state.windows[index].tabs = []
        state.windows[index].selected = nil
    }

    private func removeWindow(_ id: UUID) {
        closeTabs(in: id)
        state.windows.removeAll { $0.id == id }
    }

    private func mutateTab(_ tab: UUID, _ change: (inout WorkspaceTab) -> Void) {
        guard let windowIndex = index(ofWindowContaining: tab), let index = state.windows[windowIndex].tabs.firstIndex(where: { $0.id == tab }) else { return }
        change(&state.windows[windowIndex].tabs[index])
        save()
    }

    private func remember(_ path: String) {
        state.recents.removeAll { $0 == path }
        state.recents.insert(path, at: 0)
        if state.recents.count > Self.maxRecents { state.recents.removeLast(state.recents.count - Self.maxRecents) }
    }

    private func save() {
        saveTask = nil
        var stored = state
        for windowIndex in stored.windows.indices {
            if let frame = liveFrames[stored.windows[windowIndex].id] {
                stored.windows[windowIndex].frame = frame
            }
            for tabIndex in stored.windows[windowIndex].tabs.indices {
                if let view = viewSnapshot?(stored.windows[windowIndex].tabs[tabIndex].id) {
                    stored.windows[windowIndex].tabs[tabIndex].view = view
                }
            }
        }
        if let data = try? JSONEncoder().encode(stored) {
            defaults.set(data, forKey: key)
        }
    }

    private static func migrated(_ legacy: LegacyWorkspaceTabsState, defaults: UserDefaults) -> WorkspaceLayoutState {
        let section = defaults.string(forKey: AppSection.storageKey) ?? AppSection.default.rawValue
        let tabs = legacy.open.map { path in
            WorkspaceTab(path: path, project: legacy.projects[path].flatMap(UUID.init(uuidString:)), section: section)
        }
        var window = WorkspaceWindow(tabs: tabs, frameName: legacyFrameName)
        window.selected = tabs.first { $0.path == legacy.selected }?.id ?? tabs.first?.id
        window.sidebarVisible = defaults.object(forKey: SidebarState.storageKey) as? Bool ?? true
        return WorkspaceLayoutState(initialized: legacy.initialized, windows: [window], recents: legacy.recents)
    }

    private static func unique(_ paths: [String]) -> [String] {
        var seen = Set<String>()
        return paths.filter { seen.insert($0).inserted }
    }
}

enum WorkspaceTabStatus: Equatable, Sendable {
    case open(key: String)
    case opening
    case failed(String)
    case absent

    init(path: String, in status: DaemonStatus?) {
        guard let status, let workspace = status.workspaces.first(where: { WorkspacePath.same($0.path, path) }) else {
            self = .absent
            return
        }
        switch workspace.state {
        case "open":
            self = workspace.key.map { .open(key: $0) } ?? .opening
        case "opening":
            self = .opening
        default:
            self = .failed(workspace.error ?? "")
        }
    }
}

enum ProjectChoice {
    static func resolve(_ projects: [ProjectFields], stored: UUID?) -> ProjectFields? {
        projects.first { $0.id == stored } ?? projects.first
    }
}
