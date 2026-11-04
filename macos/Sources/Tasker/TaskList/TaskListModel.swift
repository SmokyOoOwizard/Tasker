import Foundation
import Observation
import TaskerAPI

@MainActor
final class TaskNode: Hashable {
    enum Kind: Hashable {
        case task
        case group
    }

    let key: String
    let kind: Kind
    var row: TaskRow?
    var groupStatus: UUID?
    var groupCount: Int?
    weak var parent: TaskNode?
    var children: [TaskNode]?
    let depth: Int

    init(task row: TaskRow, parent: TaskNode?, depth: Int) {
        key = (parent.map { $0.key + "/" } ?? "") + row.id.uuidString
        kind = .task
        self.row = row
        self.parent = parent
        self.depth = depth
    }

    init(group status: UUID) {
        key = "group:" + status.uuidString
        kind = .group
        groupStatus = status
        children = []
        depth = 0
    }

    nonisolated static func == (lhs: TaskNode, rhs: TaskNode) -> Bool { lhs === rhs }
    nonisolated func hash(into hasher: inout Hasher) { hasher.combine(ObjectIdentifier(self)) }

    var isExpandable: Bool {
        kind == .group || (row?.treeChildCount ?? 0) > 0
    }

    func hasAncestor(task id: UUID) -> Bool {
        var current = parent
        while let node = current {
            if node.row?.id == id { return true }
            current = node.parent
        }
        return false
    }
}

enum TaskListChange: Equatable {
    case reload
    case rows([TaskNode])
    case inserted(parent: TaskNode?, indexes: IndexSet)
    case children(TaskNode)
}

enum TaskListPhase: Equatable {
    case loading
    case ready
    case failed(String)
}

@MainActor
@Observable
final class TaskListModel {
    static let pageSize = 200
    static let chipLimit = 3
    static let refreshDelay: Duration = .milliseconds(120)

    let project: UUID
    private(set) var vocabulary: TaskListVocabulary = .empty
    private(set) var preferences: TaskListPreferences
    private(set) var phase: TaskListPhase = .loading
    private(set) var totalCount: Int?
    private(set) var roots: [TaskNode] = []
    private(set) var loadingMore = false
    var filter: TaskListFilter = .none {
        didSet { if filter != oldValue, started { reload() } }
    }

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onChange: ((TaskListChange) -> Void)?
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onOpen: ((TaskRow) -> Void)?
    @ObservationIgnored var onPeek: ((TaskRow) -> Void)?

    @ObservationIgnored private let source: any TaskListSource
    @ObservationIgnored private let settings: TaskListSettings?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var loadedCount = 0
    @ObservationIgnored private var levelCount = 0
    @ObservationIgnored private var loadingChildren: Set<String> = []
    @ObservationIgnored private var refreshTask: Task<Void, Never>?
    @ObservationIgnored private var refreshing = false
    @ObservationIgnored private var refreshAgain = false
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var started = false

    init(project: UUID, source: any TaskListSource, settings: TaskListSettings?) {
        self.project = project
        self.source = source
        self.settings = settings
        preferences = settings?.preferences(for: project) ?? TaskListPreferences()
    }

    var mode: TaskListMode { preferences.mode }

    var request: TaskListRequest {
        TaskListRequest(filter: filter, sort: mode == .groups ? TaskSort.grouped(preferences.sort) : preferences.sort)
    }

    var hasMore: Bool { loadedCount < levelCount }

    func start() {
        started = true
        reload()
    }

    func stop() {
        started = false
        loadTask?.cancel()
        refreshTask?.cancel()
        generation += 1
    }

    func apply(_ preferences: TaskListPreferences) {
        let old = self.preferences
        guard preferences != old else { return }
        self.preferences = preferences
        if preferences.mode != old.mode || preferences.sort != old.sort {
            if started { reload() }
        } else if preferences.columns != old.columns {
            onChange?(.rows(allNodes()))
        }
    }

    func update(_ change: (inout TaskListPreferences) -> Void) {
        var value = preferences
        change(&value)
        settings?.update(project, { $0 = value })
        apply(value)
    }

    func reload() {
        generation += 1
        let current = generation
        loadTask?.cancel()
        refreshTask?.cancel()
        phase = roots.isEmpty ? .loading : phase
        loadTask = Task { [weak self] in
            await self?.loadFirst(current)
        }
    }

    private func loadFirst(_ current: Int) async {
        do {
            let vocabulary = try await source.vocabulary(project: project)
            let page = try await fetchLevel(parent: nil, offset: 0)
            guard current == generation else { return }
            self.vocabulary = vocabulary
            roots = []
            loadedCount = 0
            levelCount = page.levelCount
            totalCount = page.totalCount
            append(page.rows)
            phase = .ready
            onChange?(.reload)
            expandRemembered(roots)
            await loadCounts(current)
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            phase = .failed(error.localizedDescription)
        }
    }

    func loadMore() {
        guard phase == .ready, hasMore, !loadingMore else { return }
        loadingMore = true
        let current = generation
        Task { [weak self] in
            await self?.loadNextPage(current)
        }
    }

    private func loadNextPage(_ current: Int) async {
        defer { if current == generation { loadingMore = false } }
        do {
            let page = try await fetchLevel(parent: nil, offset: loadedCount)
            guard current == generation else { return }
            levelCount = page.levelCount
            totalCount = page.totalCount
            let before = roots.count
            let groupSizes = roots.map { $0.children?.count ?? 0 }
            append(page.rows)
            if mode == .groups {
                for (index, group) in roots.enumerated() where index < before {
                    let old = groupSizes[index]
                    let new = group.children?.count ?? 0
                    if new > old { onChange?(.inserted(parent: group, indexes: IndexSet(old..<new))) }
                }
            }
            if roots.count > before { onChange?(.inserted(parent: nil, indexes: IndexSet(before..<roots.count))) }
            if mode == .tree { expandRemembered(Array(roots[before...])) }
            if mode == .groups { await loadCounts(current) }
        } catch {
            guard current == generation else { return }
            onNotice?(localizer.string(.taskListLoadFailed, error.localizedDescription))
        }
    }

    private func fetchLevel(parent: UUID?, offset: Int) async throws -> TaskRowPage {
        switch mode {
        case .tree:
            return try await source.tree(project: project, request: request, parent: parent, offset: offset, limit: Self.pageSize)
        case .flat, .groups:
            return try await source.flat(project: project, request: request, offset: offset, limit: Self.pageSize)
        }
    }

    private func append(_ rows: [TaskRow]) {
        loadedCount += rows.count
        switch mode {
        case .flat, .tree:
            roots += rows.map { TaskNode(task: $0, parent: nil, depth: 0) }
        case .groups:
            var groups = Dictionary(roots.compactMap { node in node.groupStatus.map { ($0, node) } }, uniquingKeysWith: { first, _ in first })
            for row in rows {
                let group = groups[row.statusId] ?? {
                    let node = TaskNode(group: row.statusId)
                    groups[row.statusId] = node
                    roots.append(node)
                    return node
                }()
                group.children?.append(TaskNode(task: row, parent: group, depth: 1))
            }
        }
    }

    private func loadCounts(_ current: Int) async {
        guard mode == .groups else { return }
        let groups = roots.filter { $0.kind == .group }
        let request = request
        let project = project
        let source = source
        let counts = await withTaskGroup(of: (UUID, Int?).self) { tasks in
            for group in groups {
                guard let status = group.groupStatus else { continue }
                tasks.addTask { (status, try? await source.count(project: project, request: request, status: status)) }
            }
            var result: [UUID: Int] = [:]
            for await (status, count) in tasks { if let count { result[status] = count } }
            return result
        }
        guard current == generation else { return }
        var changed: [TaskNode] = []
        for group in groups {
            if let status = group.groupStatus, let count = counts[status], group.groupCount != count {
                group.groupCount = count
                changed.append(group)
            }
        }
        if !changed.isEmpty { onChange?(.rows(changed)) }
    }

    func shouldExpand(_ node: TaskNode) -> Bool {
        switch node.kind {
        case .group:
            return !(node.groupStatus.map(preferences.collapsedGroups.contains) ?? false)
        case .task:
            guard mode == .tree, let id = node.row?.id, node.isExpandable, node.depth < TaskHierarchyLimits.maxDepth - 1 else { return false }
            return preferences.expanded.contains(id) && !node.hasAncestor(task: id)
        }
    }

    func setExpanded(_ node: TaskNode, _ expanded: Bool) {
        switch node.kind {
        case .group:
            guard let status = node.groupStatus else { return }
            update { $0.setCollapsed(group: status, !expanded) }
        case .task:
            guard let id = node.row?.id else { return }
            update { $0.setExpanded(id, expanded) }
            if expanded { loadChildren(node) }
        }
    }

    private func expandRemembered(_ nodes: [TaskNode]) {
        guard mode == .tree else { return }
        for node in nodes where shouldExpand(node) {
            loadChildren(node)
        }
    }

    func loadChildren(_ node: TaskNode) {
        guard mode == .tree, node.children == nil, let id = node.row?.id, !loadingChildren.contains(node.key) else { return }
        loadingChildren.insert(node.key)
        let current = generation
        Task { [weak self] in
            guard let self else { return }
            defer { loadingChildren.remove(node.key) }
            do {
                let rows = try await self.children(of: id)
                guard current == generation else { return }
                node.children = rows.map { TaskNode(task: $0, parent: node, depth: node.depth + 1) }
                onChange?(.children(node))
                expandRemembered(node.children ?? [])
            } catch {
                guard current == generation else { return }
                onNotice?(localizer.string(.taskListLoadFailed, error.localizedDescription))
            }
        }
    }

    private func children(of id: UUID) async throws -> [TaskRow] {
        var rows: [TaskRow] = []
        var offset = 0
        while true {
            let page = try await source.tree(project: project, request: request, parent: id, offset: offset, limit: Self.pageSize)
            rows += page.rows
            offset += Self.pageSize
            if page.rows.isEmpty || offset >= page.levelCount { return rows }
        }
    }

    func scheduleRefresh() {
        guard phase == .ready else {
            if case .failed = phase { reload() }
            return
        }
        if refreshing {
            refreshAgain = true
            return
        }
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            try? await Task.sleep(for: Self.refreshDelay)
            guard !Task.isCancelled else { return }
            await self?.refresh()
        }
    }

    func refresh() async {
        let current = generation
        refreshing = true
        defer {
            refreshing = false
            if refreshAgain {
                refreshAgain = false
                scheduleRefresh()
            }
        }
        do {
            let vocabulary = try await source.vocabulary(project: project)
            let pages = try await refreshPages()
            var rows: [TaskRow] = []
            for page in pages {
                rows += page.rows
                if page.rows.count < Self.pageSize { break }
            }
            let total = pages.first?.totalCount ?? 0
            let level = pages.first?.levelCount ?? 0
            var childRows: [String: [TaskRow]] = [:]
            if mode == .tree {
                var byTask: [UUID: [TaskRow]] = [:]
                for node in loadedParents() {
                    guard let id = node.row?.id else { continue }
                    if let cached = byTask[id] {
                        childRows[node.key] = cached
                    } else {
                        let loaded = try await children(of: id)
                        byTask[id] = loaded
                        childRows[node.key] = loaded
                    }
                }
            }
            guard current == generation else { return }
            let vocabularyChanged = vocabulary != self.vocabulary
            self.vocabulary = vocabulary
            totalCount = total
            levelCount = level
            let structural = rebuild(rows, childRows: childRows)
            if structural || vocabularyChanged {
                onChange?(.reload)
                expandRemembered(roots)
            } else {
                let changed = changedNodes
                if !changed.isEmpty { onChange?(.rows(changed)) }
            }
            changedNodes = []
            await loadCounts(current)
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            onNotice?(localizer.string(.taskListLoadFailed, error.localizedDescription))
        }
    }

    @ObservationIgnored private var changedNodes: [TaskNode] = []
    @ObservationIgnored var visibleCount = 0

    static let refreshConcurrency = 6

    var refreshLimit: Int {
        let visible = ((visibleCount + Self.pageSize) / Self.pageSize) * Self.pageSize
        return max(Self.pageSize, min(loadedCount, visible))
    }

    private func refreshPages() async throws -> [TaskRowPage] {
        let offsets = Array(stride(from: 0, to: refreshLimit, by: Self.pageSize))
        let source = source
        let project = project
        let request = request
        let tree = mode == .tree
        let limit = Self.pageSize
        return try await withThrowingTaskGroup(of: (Int, TaskRowPage).self) { group in
            var pages: [Int: TaskRowPage] = [:]
            var queue = offsets.makeIterator()
            func add(_ offset: Int) {
                group.addTask {
                    let page = tree
                        ? try await source.tree(project: project, request: request, parent: nil, offset: offset, limit: limit)
                        : try await source.flat(project: project, request: request, offset: offset, limit: limit)
                    return (offset, page)
                }
            }
            for _ in 0..<Self.refreshConcurrency { if let offset = queue.next() { add(offset) } }
            while let (offset, page) = try await group.next() {
                pages[offset] = page
                if let next = queue.next() { add(next) }
            }
            return offsets.compactMap { pages[$0] }
        }
    }

    private func loadedParents() -> [TaskNode] {
        var result: [TaskNode] = []
        func walk(_ nodes: [TaskNode]) {
            for node in nodes where node.kind == .task && node.children != nil {
                result.append(node)
                walk(node.children ?? [])
            }
        }
        walk(roots)
        return result
    }

    private func rebuild(_ rows: [TaskRow], childRows: [String: [TaskRow]]) -> Bool {
        var old: [String: TaskNode] = [:]
        for node in allNodes() { old[node.key] = node }
        let before = signature()
        changedNodes = []

        func reuse(_ row: TaskRow, parent: TaskNode?, depth: Int) -> TaskNode {
            let probe = TaskNode(task: row, parent: parent, depth: depth)
            if let existing = old[probe.key], existing.depth == depth {
                if existing.row != row {
                    existing.row = row
                    changedNodes.append(existing)
                }
                existing.parent = parent
                return existing
            }
            return probe
        }

        func attach(_ node: TaskNode) {
            guard let children = childRows[node.key] else {
                node.children = nil
                return
            }
            node.children = children.map { reuse($0, parent: node, depth: node.depth + 1) }
            node.children?.forEach(attach)
        }

        loadedCount = rows.count
        switch mode {
        case .flat, .tree:
            roots = rows.map { reuse($0, parent: nil, depth: 0) }
            if mode == .tree { roots.forEach(attach) }
        case .groups:
            var groups: [UUID: TaskNode] = [:]
            var order: [TaskNode] = []
            for row in rows {
                let group = groups[row.statusId] ?? {
                    let node = old["group:" + row.statusId.uuidString] ?? TaskNode(group: row.statusId)
                    node.children = []
                    groups[row.statusId] = node
                    order.append(node)
                    return node
                }()
                group.children?.append(reuse(row, parent: group, depth: 1))
            }
            roots = order
        }
        return signature() != before
    }

    private func signature() -> [String] {
        allNodes().map { $0.key + ($0.children == nil ? "" : "+") }
    }

    func allNodes() -> [TaskNode] {
        var result: [TaskNode] = []
        func walk(_ nodes: [TaskNode]) {
            for node in nodes {
                result.append(node)
                if let children = node.children { walk(children) }
            }
        }
        walk(roots)
        return result
    }

    func nodes(task id: UUID) -> [TaskNode] {
        allNodes().filter { $0.row?.id == id }
    }

    func open(_ node: TaskNode) {
        if let row = node.row { onOpen?(row) }
    }

    func peek(_ node: TaskNode) {
        if let row = node.row { onPeek?(row) }
    }

    func statusOptions(_ node: TaskNode) -> [TaskListVocabulary.Status] {
        guard let row = node.row else { return [] }
        return vocabulary.statusOptions(forType: row.typeId)
    }

    func changeStatus(_ node: TaskNode, to status: UUID) {
        guard let original = node.row, original.statusId != status else { return }
        let reference = vocabulary.reference(original)
        let targets = nodes(task: original.id)
        for target in targets { target.row?.statusId = status }
        onChange?(.rows(targets))
        Task { [weak self] in
            guard let self else { return }
            do {
                try await source.setStatus(project: project, task: original.id, status: status, version: original.version)
                scheduleRefresh()
            } catch {
                for target in nodes(task: original.id) where target.row?.version == original.version {
                    target.row?.statusId = original.statusId
                }
                onChange?(.rows(nodes(task: original.id)))
                onNotice?(TaskListModel.statusFailure(error, reference: reference, l: localizer))
                scheduleRefresh()
            }
        }
    }

    static func statusFailure(_ error: any Error, reference: String, l: Localizer) -> String {
        guard let error = error as? TaskerAPIError else {
            return l.string(.taskListStatusFailed, reference, error.localizedDescription)
        }
        switch error {
        case .conflict(let conflict):
            switch conflict.kind {
            case .locked:
                return l.string(.taskListStatusLocked, reference, conflict.heldBy?.name ?? conflict.message)
            case .modified:
                return l.string(.taskListStatusModified, reference)
            default:
                return l.string(.taskListStatusFailed, reference, conflict.message)
            }
        case .notFound:
            return l.string(.taskListStatusDeleted, reference)
        case .forbidden(let message):
            return l.string(.taskListStatusForbidden, reference, message)
        case .timedOut, .network:
            return l.string(.taskListStatusOffline, reference)
        default:
            return l.string(.taskListStatusFailed, reference, error.message)
        }
    }
}

enum TaskHierarchyLimits {
    static let maxDepth = 10
}
