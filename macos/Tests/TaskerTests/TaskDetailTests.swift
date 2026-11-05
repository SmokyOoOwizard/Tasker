import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubDetailSource: TaskDetailSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _detail: TaskDetailData
    private var _failures: [TaskerAPIError] = []
    private var _calls: [String] = []
    let vocab: TaskListVocabulary

    init(detail: TaskDetailData, vocabulary: TaskListVocabulary) {
        _detail = detail
        vocab = vocabulary
    }

    var calls: [String] { lock.withLock { _calls } }
    var detail: TaskDetailData {
        get { lock.withLock { _detail } }
        set { lock.withLock { _detail = newValue } }
    }

    func failNext(_ error: TaskerAPIError) { lock.withLock { _failures.append(error) } }

    private func record(_ name: String) throws {
        try lock.withLock {
            _calls.append(name)
            if !_failures.isEmpty { throw _failures.removeFirst() }
        }
    }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { vocab }
    func load(project: UUID, task: UUID) async throws -> TaskDetailData? { detail }
    func update(project: UUID, task: UUID, request: TaskUpdateRequest) async throws {
        try record("update:\(request.title ?? "")|\(request.statusId?.uuidString ?? "")|\(request.version ?? "")")
    }
    func addLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws {
        try record("addLink:\(task == target ? "self" : task.uuidString.prefix(4))|\(version ?? "nil")")
    }
    func removeLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws { try record("removeLink") }
    func addToSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws { try record("addSeries") }
    func removeFromSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws { try record("removeSeries") }
    func renumber(project: UUID, task: UUID, series: UUID, to number: Int?, version: String?) async throws { try record("renumber:\(number ?? 0)") }
    func delete(project: UUID, task: UUID, version: String?) async throws { try record("delete") }
    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief] { [] }
    func search(project: UUID) async throws -> [TaskLinkedBrief] { [] }
}

@MainActor
struct TaskDetailTests {
    let project = UUID()
    let task = UUID(uuidString: "11111111-0000-0000-0000-000000000000")!
    let other = UUID(uuidString: "22222222-0000-0000-0000-000000000000")!
    let todo = UUID()
    let doing = UUID()
    let done = UUID()
    let cancelled = UUID()
    let flow = UUID()
    let simple = UUID()
    let feature = UUID()
    let chore = UUID()
    let series = UUID()
    let blocks = UUID()
    let parent = UUID()

    var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = [
            .init(id: todo, name: "Todo", color: "#8993a4"), .init(id: doing, name: "Doing", color: "#2684ff"),
            .init(id: done, name: "Done", color: "#36b37e"), .init(id: cancelled, name: "Cancelled", color: "#de350b")
        ]
        value.statusSets = [flow: [todo, doing, done], simple: [todo, cancelled]]
        value.types = [feature: .init(name: "Feature", statusSetId: flow), chore: .init(name: "Chore", statusSetId: simple)]
        value.typeOrder = [feature, chore]
        value.prefixes = [series: "DMO"]
        value.seriesNames = [series: "Demo"]
        value.seriesOrder = [series]
        value.linkTypes = [
            .init(id: blocks, name: "Blocks", outwardName: "blocks", inwardName: "is blocked by", hierarchical: false, isSymmetric: false),
            .init(id: parent, name: "Parent", outwardName: "includes", inwardName: "part of", hierarchical: true, isSymmetric: false)
        ]
        return value
    }

    func link(_ type: UUID, _ direction: LinkDirection, name: String, status: UUID, id: UUID = UUID()) -> TaskLinkEntry {
        TaskLinkEntry(typeId: type, typeName: "", direction: direction, name: name,
                      task: TaskLinkedBrief(id: id, title: "T", statusId: status, typeId: nil, seriesNumbers: []))
    }

    func detail(status: UUID? = nil, links: [TaskLinkEntry] = []) -> TaskDetailData {
        TaskDetailData(
            id: task, projectId: project, title: "Title", description: "", typeId: feature, statusId: status ?? todo,
            seriesNumbers: [TaskSeriesRef(seriesId: series, number: 1)],
            createdAt: Date(timeIntervalSince1970: 0), updatedAt: Date(timeIntervalSince1970: 0), version: "v1", fields: [], links: links)
    }

    func ready(_ source: StubDetailSource) async -> TaskDetailModel {
        let model = TaskDetailModel(project: project, task: task, source: source)
        model.load()
        for _ in 0..<100 where model.phase == .loading { try? await Task.sleep(for: .milliseconds(10)) }
        return model
    }

    @Test func loadsAndGroupsLinks() async {
        let links = [
            link(blocks, .outward, name: "blocks", status: todo), link(blocks, .inward, name: "is blocked by", status: todo),
            link(blocks, .outward, name: "blocks", status: done),
            link(parent, .outward, name: "includes", status: done), link(parent, .outward, name: "includes", status: todo),
            link(parent, .inward, name: "part of", status: todo)
        ]
        let model = await ready(StubDetailSource(detail: detail(links: links), vocabulary: vocabulary))
        #expect(model.phase == .ready)
        #expect(model.linkGroups.map(\.name) == ["blocks", "is blocked by"])
        #expect(model.linkGroups[0].entries.count == 2)
        #expect(model.parents.count == 1)
        #expect(model.children.count == 2)
        #expect(model.childProgress.done == 1)
        #expect(model.childProgress.total == 2)
        #expect(model.reference == "DMO-1")
    }

    @Test func statusOptionsFollowType() async {
        let model = await ready(StubDetailSource(detail: detail(), vocabulary: vocabulary))
        #expect(model.statusOptions.map(\.name) == ["Todo", "Doing", "Done"])
        #expect(model.vocabulary.statusNeedsChoice(current: doing, newType: chore))
        #expect(!model.vocabulary.statusNeedsChoice(current: todo, newType: chore))
        #expect(model.freeSeries.isEmpty)
    }

    @Test func modifiedConflictRereadsAndOffersReplay() async {
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = await ready(source)
        source.failNext(TaskerAPIError.modified())
        var newer = detail()
        newer.version = "v2"
        newer.title = "Changed by someone"
        source.detail = newer
        await model.run(.title("Mine"))
        #expect(model.conflict?.edit == .title("Mine"))
        #expect(model.detail?.version == "v2")
        model.resolveConflict(keepMine: true)
        for _ in 0..<100 where source.calls.count < 2 { try? await Task.sleep(for: .milliseconds(10)) }
        #expect(source.calls.last == "update:Mine||v2")
        #expect(model.conflict == nil)
    }

    @Test func keepingOthersDropsTheEdit() async {
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = await ready(source)
        source.failNext(TaskerAPIError.modified())
        await model.run(.title("Mine"))
        model.resolveConflict(keepMine: false)
        try? await Task.sleep(for: .milliseconds(50))
        #expect(source.calls.count == 1)
        #expect(model.conflict == nil)
    }

    @Test func lockedShowsNotice() async {
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = await ready(source)
        var notices: [String] = []
        model.onFailure = { notices.append($0) }
        source.failNext(TaskerAPIError.locked(by: "Anna"))
        await model.run(.status(doing))
        #expect(notices.count == 1)
        #expect(notices[0].contains("Anna"))
        #expect(model.conflict == nil)
    }

    @Test func inwardLinkIsCreatedFromTheOtherTask() async {
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = await ready(source)
        await model.run(.addLink(type: blocks, target: other, outward: false))
        #expect(source.calls == ["addLink:2222|nil"])
        await model.run(.addLink(type: blocks, target: other, outward: true))
        #expect(source.calls.last == "addLink:1111|v1")
    }

    @Test func deleteReportsAndStops() async {
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = await ready(source)
        var deleted = false
        model.onDeleted = { deleted = true }
        await model.run(.delete)
        #expect(deleted)
    }

    @Test func missingTask() async {
        struct Missing: Error {}
        let source = StubDetailSource(detail: detail(), vocabulary: vocabulary)
        let model = TaskDetailModel(project: project, task: task, source: MissingSource(base: source))
        model.load()
        for _ in 0..<100 where model.phase == .loading { try? await Task.sleep(for: .milliseconds(10)) }
        #expect(model.phase == .missing)
    }

    @Test func fieldCodec() {
        #expect(FieldValueCodec.normalize(" 42 ", type: .int) == "42")
        #expect(FieldValueCodec.normalize("4.5x", type: .int) == nil)
        #expect(FieldValueCodec.normalize("1,5", type: .float) == "1.5")
        #expect(FieldValueCodec.normalize("abc", type: .float) == nil)
        #expect(FieldValueCodec.normalize("2026-02-30", type: .date) == nil)
        #expect(FieldValueCodec.normalize("2026-03-04", type: .date) == "2026-03-04")
        #expect(FieldValueCodec.normalize("yes", type: .bool) == nil)
        #expect(FieldValueCodec.normalizeAll(["1", "", "2"], type: .int) == ["1", "2"])
        #expect(FieldValueCodec.normalizeAll(["1", "x"], type: .int) == nil)
        let date = FieldValueCodec.day(from: "2026-11-04")!
        #expect(FieldValueCodec.dayString(date) == "2026-11-04")
    }

    @Test func markdownBlocks() {
        let blocks = MarkdownParser.parse("# Head\n\ntext one\ntext two\n\n- a\n  - b\n1. c\n- [x] d\n\n> q\n\n```\ncode\nmore\n```\n---")
        #expect(blocks == [
            .heading(level: 1, text: "Head"),
            .paragraph("text one\ntext two"),
            .listItem(marker: "•", level: 0, text: "a"),
            .listItem(marker: "•", level: 1, text: "b"),
            .listItem(marker: "1.", level: 0, text: "c"),
            .listItem(marker: "☑", level: 0, text: "d"),
            .quote("q"),
            .code("code\nmore"),
            .rule
        ])
    }

    @Test func referencesBecomeLinks() {
        let text = MarkdownParser.inline("See DMO-12, UTF-8 and `DMO-3` **DMO-4**", prefixes: ["DMO"])
        let links = text.runs.compactMap { run in run.link.flatMap(TaskReference.ref) }
        #expect(links == ["DMO-12", "DMO-4"])
        #expect(TaskReference.ref(from: TaskReference.url("DMO-7")!) == "DMO-7")
    }

    @Test func navigatorHistory() {
        let nav = TaskNavigator()
        let a = UUID(), b = UUID()
        nav.togglePeek(a)
        #expect(nav.peek == a)
        nav.togglePeek(a)
        #expect(nav.peek == nil)
        nav.showPeek(a)
        nav.open(a)
        #expect(nav.peek == nil && nav.card == a)
        nav.open(b)
        #expect(nav.canGoBack)
        nav.back()
        #expect(nav.card == a)
        nav.back()
        #expect(nav.card == nil)
        let c = UUID()
        nav.open(a)
        nav.open(b)
        nav.open(c)
        #expect(nav.history == [a, b])
        nav.jump(toHistory: 0)
        #expect(nav.card == a && nav.history.isEmpty)
    }
}

struct MissingSource: TaskDetailSource {
    let base: StubDetailSource
    func vocabulary(project: UUID) async throws -> TaskListVocabulary { base.vocab }
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
}
