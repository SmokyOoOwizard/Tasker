import Foundation
import Testing
import TaskerAPI
@testable import Tasker

actor StubBoardSource: BoardEditorSource {
    var catalogValue = BoardCatalog()
    var stored: BoardRecord?
    var lockState: BoardLockState = .none
    var acquireFailure: (any Error)?
    var saveFailure: (any Error)?
    private(set) var acquired = 0
    private(set) var released = 0
    private(set) var created: [BoardDraft] = []
    private(set) var updated: [BoardDraft] = []

    func setCatalog(_ value: BoardCatalog) { catalogValue = value }
    func setStored(_ value: BoardRecord?) { stored = value }
    func setAcquireFailure(_ value: (any Error)?) { acquireFailure = value }
    func setSaveFailure(_ value: (any Error)?) { saveFailure = value }
    func setLock(_ value: BoardLockState) { lockState = value }

    func catalog(project: UUID) async throws -> BoardCatalog { catalogValue }
    func board(project: UUID, id: UUID) async throws -> BoardRecord? { stored }

    func create(project: UUID, draft: BoardDraft, catalog: BoardCatalog) async throws -> UUID {
        if let saveFailure { throw saveFailure }
        created.append(draft)
        return UUID()
    }

    func update(project: UUID, record: BoardRecord, draft: BoardDraft, catalog: BoardCatalog) async throws {
        if let saveFailure { throw saveFailure }
        updated.append(draft)
        stored = BoardRecord(id: record.id, version: record.version + "1", draft: draft)
    }

    func lock(project: UUID, id: UUID) async throws -> BoardLockState { lockState }

    func acquire(project: UUID, id: UUID) async throws {
        if let acquireFailure { throw acquireFailure }
        acquired += 1
    }

    func release(project: UUID, id: UUID) async throws { released += 1 }
}

@MainActor
struct BoardEditorTests {
    let project = UUID()
    let todo = UUID()
    let doing = UUID()
    let done = UUID()
    let set = UUID()
    let otherSet = UUID()
    let otherStatus = UUID()
    let l = Localizer(language: .en)

    var catalog: BoardCatalog {
        var value = BoardCatalog()
        value.statuses = [
            .init(id: todo, name: "Todo", color: "#8993a4"), .init(id: doing, name: "Doing", color: "#2684ff"),
            .init(id: done, name: "Done", color: "#36b37e"), .init(id: otherStatus, name: "Backlog", color: "#8993a4")
        ]
        value.sets = [.init(id: set, name: "Main", statusIds: [todo, doing, done]), .init(id: otherSet, name: "Other", statusIds: [otherStatus, done])]
        return value
    }

    func existing() -> BoardRecord {
        BoardRecord(id: UUID(), version: "v", draft: BoardDraft(
            name: "Main", setIds: [set],
            columns: [BoardColumnDraft(serverId: UUID(), name: "Open", statusIds: [todo, doing], drop: [set: todo])]))
    }

    func settle(_ model: BoardEditorModel, until check: @MainActor () -> Bool) async {
        for _ in 0..<400 {
            if check() { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    func editor(
        _ source: StubBoardSource, board: UUID?, heartbeat: Duration = .seconds(60), queries: StubTaskQuerySource? = nil
    ) -> BoardEditorModel {
        let model = BoardEditorModel(
            target: BoardEditorTarget(project: project, board: board), source: source, querySource: queries, heartbeat: heartbeat)
        model.localizer = l
        return model
    }

    @Test func newBoardStartsWithColumnsFromFirstSet() async {
        let source = StubBoardSource()
        await source.setCatalog(catalog)
        let model = editor(source, board: nil)
        model.load()
        await settle(model) { model.phase == .ready }
        #expect(model.draft.setIds == [set])
        #expect(model.draft.columns.map(\.name) == ["Todo", "Doing", "Done"])
        #expect(model.draft.columns.map(\.statusIds) == [[todo], [doing], [done]])
        #expect(!model.isDirty)
        #expect(await source.acquired == 0)
    }

    @Test func creatingRequiresNameAndPostsDraft() async {
        let source = StubBoardSource()
        await source.setCatalog(catalog)
        let model = editor(source, board: nil)
        model.load()
        await settle(model) { model.phase == .ready }
        #expect(await model.save() == false)
        #expect(model.issues.board == [l.string(.boardNameRequired)])
        model.draft.name = " Team "
        var created = false
        model.onCreated = { created = true }
        #expect(await model.save())
        #expect(created)
        #expect(await source.created.count == 1)
    }

    @Test func editingTakesLockAndReleaseGivesItBack() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        #expect(model.lock == .mine)
        #expect(model.draft == record.draft)
        model.release()
        await settle(model) { true }
        try? await Task.sleep(for: .milliseconds(50))
        #expect(await source.released == 1)
    }

    @Test func heartbeatRenewsTheLock() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id, heartbeat: .milliseconds(20))
        model.load()
        await settle(model) { model.lock == .mine }
        try? await Task.sleep(for: .milliseconds(150))
        #expect(await source.acquired >= 3)
        model.release()
    }

    @Test func foreignLockBlocksSaving() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        await source.setAcquireFailure(TaskerAPIError.locked(by: "Claude"))
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.holder != nil }
        #expect(model.holder == "Claude")
        #expect(!model.canSave)
        model.release()
        try? await Task.sleep(for: .milliseconds(30))
        #expect(await source.released == 0)
    }

    @Test func lockIsTakenAfterOtherReleasesIt() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        await source.setAcquireFailure(TaskerAPIError.locked(by: "Claude"))
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.holder != nil }
        await source.setAcquireFailure(nil)
        await source.setLock(.none)
        model.refreshLock()
        await settle(model) { model.lock == .mine }
        #expect(model.lock == .mine)
        #expect(model.canSave)
        model.release()
    }

    @Test func serverOverlapShowsNextToTheColumn() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        model.edit { draft, _ in draft.columns.append(BoardColumnDraft(name: "Again", statusIds: [todo])) }
        let overlap = Refusal(code: "board.columnOverlap", message: "overlap", params: ["status": .string("Todo"), "otherColumn": .string("Open"), "columnIndex": .int(1)])
        await source.setSaveFailure(TaskerAPIError.badRequest(overlap))
        #expect(await model.save() == false)
        let second = model.draft.columns[1].id
        #expect(model.issues.board.isEmpty)
        #expect(model.issues.columns[second] == [model.localizer.refusal(overlap)])
        #expect(model.issues.columns[second]?.first?.contains("Todo") == true)
        model.release()
    }

    @Test func serverIssueWithoutColumnIndexStaysOnBoard() async {
        let draft = BoardDraft(name: "B", setIds: [], columns: [BoardColumnDraft(name: "A", statusIds: [])])
        let l = Localizer(language: .en)
        let inRange = BoardServerIssue.map(Refusal(code: "board.noDropRule", message: "m", params: ["column": .string("A"), "taskType": .string("T"), "columnIndex": .int(0)]), draft: draft, l: l)
        #expect(inRange.columns[draft.columns[0].id]?.count == 1)
        #expect(inRange.board.isEmpty)
        let outOfRange = BoardServerIssue.map(Refusal(code: "board.noColumns", message: "m", params: ["columnIndex": .int(5)]), draft: draft, l: l)
        #expect(outOfRange.columns.isEmpty)
        #expect(outOfRange.board == [l.refusal(Refusal(code: "board.noColumns", message: "m"))])
        let none = BoardServerIssue.map(Refusal(code: "board.noColumns", message: "m"), draft: draft, l: l)
        #expect(none.board.count == 1)
    }

    @Test func modifiedBoardAsksToReload() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        await source.setSaveFailure(TaskerAPIError.modified())
        model.draft.name = "Renamed"
        #expect(await model.save() == false)
        #expect(model.staleVersion)
        model.release()
    }

    @Test func savingExistingBoardReloadsAndClearsDirty() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        model.draft.name = "Renamed"
        #expect(model.isDirty)
        #expect(await model.save())
        #expect(!model.isDirty)
        #expect(model.draft.name == "Renamed")
        model.release()
    }

    @Test func removingASetDropsItsStatusesAndDropRules() {
        var draft = BoardDraft(name: "B", setIds: [set, otherSet], columns: [
            BoardColumnDraft(name: "Wide", statusIds: [todo, otherStatus], drop: [set: todo, otherSet: otherStatus])
        ])
        BoardEditing.toggleSet(otherSet, in: &draft, catalog: catalog)
        #expect(draft.setIds == [set])
        #expect(draft.columns[0].statusIds == [todo])
        #expect(draft.columns[0].drop == [set: todo])
    }

    @Test func uncheckingAStatusDropsItsRule() {
        var draft = BoardDraft(name: "B", setIds: [set], columns: [BoardColumnDraft(name: "C", statusIds: [todo, doing], drop: [set: doing])])
        BoardEditing.toggleStatus(doing, column: 0, in: &draft)
        #expect(draft.columns[0].statusIds == [todo])
        #expect(draft.columns[0].drop.isEmpty)
    }

    @Test func columnsMoveWithinBounds() {
        var draft = BoardDraft(name: "B", setIds: [set], columns: [BoardColumnDraft(name: "A"), BoardColumnDraft(name: "B"), BoardColumnDraft(name: "C")])
        BoardEditing.move(column: 0, by: -1, in: &draft)
        BoardEditing.move(column: 0, by: 1, in: &draft)
        BoardEditing.move(column: 2, by: 1, in: &draft)
        #expect(draft.columns.map(\.name) == ["B", "A", "C"])
    }

    @Test func unchangedQueryIsSentAsNilAndChangedOneAsText() {
        let catalog = catalog
        let original = BoardColumnDraft(serverId: UUID(), name: "A", statusIds: [todo], query: ".Field.Estimate >= 3")
        #expect(GraphQLBoardEditorSource.input(original, original: original, catalog: catalog).query == nil)
        var spaced = original
        spaced.query = "  .Field.Estimate >= 3 "
        #expect(spaced == original)
        #expect(GraphQLBoardEditorSource.input(spaced, original: original, catalog: catalog).query == nil)
        var changed = original
        changed.query = ".Field.Estimate >= 5 | sort -.Updated"
        #expect(GraphQLBoardEditorSource.input(changed, original: original, catalog: catalog).query == ".Field.Estimate >= 5 | sort -.Updated")
        var cleared = original
        cleared.query = " "
        #expect(GraphQLBoardEditorSource.input(cleared, original: original, catalog: catalog).query == "")
        #expect(GraphQLBoardEditorSource.input(BoardColumnDraft(name: "N", statusIds: []), original: nil, catalog: catalog).query == nil)
        #expect(GraphQLBoardEditorSource.input(BoardColumnDraft(name: "N", statusIds: [], query: ".Type == \"Bug\""), original: nil, catalog: catalog).query
            == ".Type == \"Bug\"")
    }

    @Test func typingAQueryEditsTheDraftAndABrokenOneIsNotSent() async {
        let source = StubBoardSource()
        await source.setCatalog(catalog)
        let record = existing()
        await source.setStored(record)
        let queries = StubTaskQuerySource()
        let broken = TaskQueryIssue(code: "query.unknownField", message: "No field 'Gone'", params: ["name": "Gone"], start: 7, end: 11)
        queries.set(".Field.Gone == 1", TaskQueryReport(valid: false, hasSort: false, errors: [broken], hints: []))
        let model = editor(source, board: record.id, queries: queries)
        model.load()
        await settle(model) { model.phase == .ready }
        let column = model.draft.columns[0]
        let query = try! #require(model.queryModel(for: column))
        query.edited(".Field.Gone == 1", cursor: 5)
        #expect(model.draft.columns[0].query == ".Field.Gone == 1")
        await settle(model) { !query.errors.isEmpty }
        #expect(query.error?.code == "query.unknownField")

        #expect(await model.save() == false)
        #expect(model.issues.columns[column.id] == [l.refusal(broken.refusal)])
        #expect(await source.updated.isEmpty)

        query.edited(".Status == \"Todo\" | sort .Title", cursor: 5)
        #expect(model.draft.columns[0].query == ".Status == \"Todo\" | sort .Title")
        #expect(model.isDirty)
        await settle(model) { query.errors.isEmpty }
        #expect(await model.save())
        #expect(await source.updated.last?.columns[0].query == ".Status == \"Todo\" | sort .Title")
    }

    @Test func rereadAndRetryKeepsTypedChangesAndSavesOnFreshVersion() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        await source.setSaveFailure(TaskerAPIError.modified())
        model.draft.name = "Renamed"
        #expect(await model.save() == false)
        #expect(model.staleVersion)
        await source.setSaveFailure(nil)
        await source.setStored(BoardRecord(id: record.id, version: "fresh", draft: record.draft))
        #expect(await model.rereadAndRetry())
        #expect(!model.staleVersion)
        #expect(await source.updated.last?.name == "Renamed")
        model.release()
    }

    @Test func rereadAndRetryDoesNothingWithoutConflict() async {
        let source = StubBoardSource()
        let record = existing()
        await source.setCatalog(catalog)
        await source.setStored(record)
        let model = editor(source, board: record.id)
        model.load()
        await settle(model) { model.lock == .mine }
        #expect(await model.rereadAndRetry() == false)
        #expect(await source.updated.isEmpty)
        model.release()
    }

    @Test func brokenUnchangedQueryDoesNotBlockSavingOtherChanges() async {
        let source = StubBoardSource()
        await source.setCatalog(catalog)
        let record = existing()
        var stored = record
        stored.draft.columns.append(BoardColumnDraft(serverId: UUID(), name: "Broken", statusIds: [done], query: ".Field.Gone == 1"))
        await source.setStored(stored)
        let queries = StubTaskQuerySource()
        let broken = TaskQueryIssue(code: "query.unknownField", message: "No field 'Gone'", params: ["name": "Gone"], start: 7, end: 11)
        queries.set(".Field.Gone == 1", TaskQueryReport(valid: false, hasSort: false, errors: [broken], hints: []))
        let model = editor(source, board: stored.id, queries: queries)
        model.load()
        await settle(model) { model.phase == .ready }
        let column = model.draft.columns[1]
        let query = try! #require(model.queryModel(for: column))
        await settle(model) { !query.errors.isEmpty }

        #expect(model.queryIssues()[column.id] == [l.refusal(broken.refusal)])
        model.edit { draft, _ in draft.name = "Renamed" }
        #expect(await model.save())
        #expect(await source.updated.last?.name == "Renamed")
        #expect(model.issues.isEmpty)
        let reloaded = try! #require(model.draft.columns.last)
        let again = try! #require(model.queryModel(for: reloaded))
        await settle(model) { !again.errors.isEmpty }
        #expect(model.queryIssues()[reloaded.id] == [l.refusal(broken.refusal)])
    }
}
