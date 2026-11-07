import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubCommentSource: TaskDetailSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _items: [TaskCommentItem] = []
    private var _calls: [String] = []
    private var _failure: TaskerAPIError?

    var items: [TaskCommentItem] {
        get { lock.withLock { _items } }
        set { lock.withLock { _items = newValue } }
    }

    var calls: [String] { lock.withLock { _calls } }

    func failNext(_ error: TaskerAPIError) { lock.withLock { _failure = error } }

    private func record(_ call: String) throws {
        try lock.withLock {
            _calls.append(call)
            if let failure = _failure {
                _failure = nil
                throw failure
            }
        }
    }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { .empty }
    func load(project: UUID, task: UUID) async throws -> TaskDetailData? { nil }
    func update(project: UUID, task: UUID, request: TaskUpdateRequest) async throws {}
    func addLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws {}
    func removeLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws {}
    func addToSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws {}
    func removeFromSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws {}
    func renumber(project: UUID, task: UUID, series: UUID, to number: Int?, version: String?) async throws {}
    func delete(project: UUID, task: UUID, version: String?) async throws {}
    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief] { [] }
    func search(project: UUID) async throws -> [TaskLinkedBrief] { [] }
    func comments(project: UUID, task: UUID) async throws -> [TaskCommentItem]? { items }
    func addComment(project: UUID, task: UUID, parent: UUID?, body: String) async throws { try record("add:\(parent != nil):\(body)") }
    func updateComment(project: UUID, task: UUID, comment: UUID, body: String, version: String) async throws { try record("update:\(body):\(version)") }
    func deleteComment(project: UUID, task: UUID, comment: UUID, version: String) async throws { try record("delete:\(version)") }
}

@MainActor
struct TaskCommentsTests {
    private func item(_ id: UUID, parent: UUID?, depth: Int, deleted: Bool = false, mine: Bool = false, name: String = "Лена") -> TaskCommentItem {
        TaskCommentItem(
            id: id, parentId: parent, depth: depth,
            author: deleted ? nil : TaskCommentAuthor(id: UUID(), name: name, kind: .human),
            body: deleted ? "" : "текст", createdAt: Date(), editedAt: nil, deleted: deleted, mine: mine, version: "v1")
    }

    private func chain(_ count: Int) -> [TaskCommentItem] {
        var parent: UUID?
        var items: [TaskCommentItem] = []
        for depth in 0..<count {
            let id = UUID()
            items.append(item(id, parent: parent, depth: depth))
            parent = id
        }
        return items
    }

    private func model(_ source: StubCommentSource) -> TaskCommentsModel {
        TaskCommentsModel(project: UUID(), task: UUID(), source: source)
    }

    private func commentIds(_ rows: [TaskCommentRow]) -> [UUID] {
        rows.compactMap {
            if case .comment(let item, _, _, _, _) = $0.kind { return item.id }
            return nil
        }
    }

    @Test func aLongBranchStopsAtTheLevelLimitAndContinuesOnDemand() async {
        let source = StubCommentSource()
        let items = chain(8)
        source.items = items
        let comments = model(source)
        _ = await comments.add(parent: nil, body: "x")

        let rows = comments.rows(maxLevel: 4)

        #expect(commentIds(rows) == Array(items.prefix(5)).map(\.id))
        #expect(rows.map(\.level).max() == 4)
        guard case .continuation(let at, let replies) = rows.last?.kind else {
            Issue.record("no continuation row")
            return
        }
        #expect(at.id == items[4].id)
        #expect(replies == 3)

        comments.openBranch(at.id)
        let focused = comments.rows(maxLevel: 4)
        #expect(commentIds(focused) == Array(items.suffix(4)).map(\.id))
        #expect(focused.first?.level == 0)

        comments.focus = nil
        #expect(commentIds(comments.rows(maxLevel: 4)).count == 5)
    }

    @Test func aCollapsedBranchHidesRepliesAndCountsThem() async {
        let source = StubCommentSource()
        let items = chain(3)
        source.items = items
        let comments = model(source)
        _ = await comments.add(parent: nil, body: "x")

        comments.toggle(items[0].id)
        let rows = comments.rows(maxLevel: 6)

        #expect(rows.count == 1)
        guard case .comment(_, let replies, let last, let collapsed, _) = rows[0].kind else {
            Issue.record("no comment row")
            return
        }
        #expect(replies == 2)
        #expect(last == "Лена")
        #expect(collapsed)
    }

    @Test func aReplyOpensAComposerUnderTheCommentAndExpandsIt() async {
        let source = StubCommentSource()
        let items = chain(2)
        source.items = items
        let comments = model(source)
        _ = await comments.add(parent: nil, body: "x")
        comments.toggle(items[0].id)

        comments.startReply(items[0].id)
        let rows = comments.rows(maxLevel: 6)

        #expect(rows.count == 3)
        guard case .composer(let target) = rows[1].kind else {
            Issue.record("no composer row")
            return
        }
        #expect(target.id == items[0].id)
        #expect(rows[1].level == 1)

        #expect(await comments.add(parent: items[0].id, body: "ответ"))
        #expect(comments.replyTo == nil)
        #expect(source.calls.last == "add:true:ответ")
    }

    @Test func editingAndDeletingSendTheVersionAndAFailureKeepsTheEditorOpen() async {
        let source = StubCommentSource()
        let comment = item(UUID(), parent: nil, depth: 0, mine: true)
        source.items = [comment]
        let comments = model(source)
        var failures: [String] = []
        comments.onFailure = { failures.append($0) }
        _ = await comments.add(parent: nil, body: "x")

        comments.startEdit(comment.id)
        source.failNext(.timedOut)
        #expect(await comments.update(comment, body: "новый") == false)
        #expect(comments.editing == comment.id)
        #expect(failures.count == 1)

        #expect(await comments.update(comment, body: "новый"))
        #expect(comments.editing == nil)
        #expect(source.calls.contains("update:новый:v1"))

        await comments.delete(comment)
        #expect(source.calls.last == "delete:v1")
    }

    @Test func aMarkWithRepliesStaysAndIsNotCounted() async {
        let source = StubCommentSource()
        let root = UUID()
        source.items = [item(root, parent: nil, depth: 0, deleted: true), item(UUID(), parent: root, depth: 1)]
        let comments = model(source)
        _ = await comments.add(parent: nil, body: "x")

        #expect(comments.count == 1)
        #expect(comments.hasReplies(root))
        #expect(commentIds(comments.rows(maxLevel: 6)).count == 2)
    }

    @Test func aTargetThatDisappearedClosesTheOpenEditors() async {
        let source = StubCommentSource()
        let items = chain(2)
        source.items = items
        let comments = model(source)
        _ = await comments.add(parent: nil, body: "x")
        comments.startReply(items[1].id)

        source.items = [items[0]]
        _ = await comments.add(parent: nil, body: "y")

        #expect(comments.replyTo == nil)
    }
}
