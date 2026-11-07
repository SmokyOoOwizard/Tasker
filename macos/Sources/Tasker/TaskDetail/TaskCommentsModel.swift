import Foundation
import Observation
import TaskerAPI

struct TaskCommentAuthor: Hashable, Sendable {
    var id: UUID
    var name: String
    var kind: UserKind
}

struct TaskCommentItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var parentId: UUID?
    var depth: Int
    var author: TaskCommentAuthor?
    var body: String
    var createdAt: Date
    var editedAt: Date?
    var deleted: Bool
    var mine: Bool
    var version: String
}

enum TaskCommentRowKind: Hashable, Sendable {
    case comment(TaskCommentItem, replies: Int, lastAuthor: String?, collapsed: Bool, hasLine: Bool)
    case composer(TaskCommentItem)
    case continuation(TaskCommentItem, replies: Int)
}

struct TaskCommentRow: Hashable, Sendable, Identifiable {
    var kind: TaskCommentRowKind
    var level: Int

    var id: String {
        switch kind {
        case .comment(let item, _, _, _, _): "c" + item.id.uuidString
        case .composer(let item): "r" + item.id.uuidString
        case .continuation(let item, _): "m" + item.id.uuidString
        }
    }
}

@MainActor
@Observable
final class TaskCommentsModel {
    static let fullLevels = 6
    static let compactLevels = 4

    private(set) var items: [TaskCommentItem] = []
    private(set) var loaded = false
    private(set) var busy = false
    var collapsed: Set<UUID> = []
    var focus: UUID?
    var replyTo: UUID?
    var editing: UUID?
    var deleting: TaskCommentItem?

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onFailure: ((String) -> Void)?

    @ObservationIgnored private let project: UUID
    @ObservationIgnored private let task: UUID
    @ObservationIgnored private let source: any TaskDetailSource
    @ObservationIgnored private var loadTask: Task<Void, Never>?

    init(project: UUID, task: UUID, source: any TaskDetailSource) {
        self.project = project
        self.task = task
        self.source = source
    }

    var count: Int { items.filter { !$0.deleted }.count }

    var focusItem: TaskCommentItem? { focus.flatMap { id in items.first { $0.id == id } } }

    func load() {
        loadTask?.cancel()
        loadTask = Task { [weak self] in await self?.fetch() }
    }

    func stop() {
        loadTask?.cancel()
    }

    func hasReplies(_ id: UUID) -> Bool {
        items.contains { $0.parentId == id }
    }

    func rows(maxLevel: Int) -> [TaskCommentRow] {
        var byParent: [UUID: [TaskCommentItem]] = [:]
        var roots: [TaskCommentItem] = []
        for item in items {
            if let parent = item.parentId {
                byParent[parent, default: []].append(item)
            } else {
                roots.append(item)
            }
        }
        func descendants(_ id: UUID) -> [TaskCommentItem] {
            (byParent[id] ?? []).flatMap { [$0] + descendants($0.id) }
        }

        var rows: [TaskCommentRow] = []
        func walk(_ list: [TaskCommentItem], level: Int) {
            for item in list {
                let children = byParent[item.id] ?? []
                let below = descendants(item.id)
                let isCollapsed = collapsed.contains(item.id) && !children.isEmpty
                let atLimit = level >= maxLevel
                rows.append(TaskCommentRow(
                    kind: .comment(
                        item, replies: below.count, lastAuthor: below.last { !$0.deleted }?.author?.name,
                        collapsed: isCollapsed, hasLine: !children.isEmpty && !isCollapsed && !atLimit),
                    level: level))
                if replyTo == item.id {
                    rows.append(TaskCommentRow(kind: .composer(item), level: min(level + 1, maxLevel)))
                }
                guard !isCollapsed, !children.isEmpty else { continue }
                if atLimit {
                    rows.append(TaskCommentRow(kind: .continuation(item, replies: below.count), level: maxLevel))
                } else {
                    walk(children, level: level + 1)
                }
            }
        }
        if let focusItem {
            walk([focusItem], level: 0)
        } else {
            walk(roots, level: 0)
        }
        return rows
    }

    func toggle(_ id: UUID) {
        if collapsed.remove(id) == nil { collapsed.insert(id) }
    }

    func startReply(_ id: UUID) {
        editing = nil
        collapsed.remove(id)
        replyTo = id
    }

    func startEdit(_ id: UUID) {
        replyTo = nil
        editing = id
    }

    func openBranch(_ id: UUID) {
        replyTo = nil
        editing = nil
        focus = id
    }

    func add(parent: UUID?, body: String) async -> Bool {
        let posted = await mutate { [project, task, source] in
            try await source.addComment(project: project, task: task, parent: parent, body: body)
        }
        if posted, parent != nil { replyTo = nil }
        return posted
    }

    func update(_ item: TaskCommentItem, body: String) async -> Bool {
        let saved = await mutate { [project, task, source] in
            try await source.updateComment(project: project, task: task, comment: item.id, body: body, version: item.version)
        }
        if saved { editing = nil }
        return saved
    }

    func delete(_ item: TaskCommentItem) async {
        _ = await mutate { [project, task, source] in
            try await source.deleteComment(project: project, task: task, comment: item.id, version: item.version)
        }
    }

    private func mutate(_ action: @escaping @Sendable () async throws -> Void) async -> Bool {
        guard !busy else { return false }
        busy = true
        defer { busy = false }
        do {
            try await action()
            await fetch()
            return true
        } catch let error as TaskerAPIError {
            report(error)
        } catch is CancellationError {
        } catch {
            onFailure?(localizer.string(.taskDetailFailed, localizer.message(for: error)))
        }
        await fetch()
        return false
    }

    private func report(_ error: TaskerAPIError) {
        switch error {
        case .conflict(let conflict):
            onFailure?(localizer.refusal(conflict.refusal))
        case .forbidden(let refusal):
            onFailure?(localizer.string(.taskDetailForbidden, localizer.refusal(refusal)))
        case .timedOut, .network:
            onFailure?(localizer.string(.taskDetailOffline))
        default:
            onFailure?(localizer.string(.taskDetailFailed, localizer.message(for: error)))
        }
    }

    private func fetch() async {
        do {
            let value = try await source.comments(project: project, task: task) ?? []
            if Task.isCancelled { return }
            items = value
            loaded = true
            if let focus, !value.contains(where: { $0.id == focus }) { self.focus = nil }
            if let replyTo, !value.contains(where: { $0.id == replyTo && !$0.deleted }) { self.replyTo = nil }
            if let editing, !value.contains(where: { $0.id == editing && !$0.deleted }) { self.editing = nil }
        } catch {
        }
    }
}
