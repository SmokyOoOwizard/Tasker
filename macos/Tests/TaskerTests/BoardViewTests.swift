import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubBoardViewSource: BoardViewSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _rows: [TaskRow]
    private var _failure: TaskerAPIError?
    private var _moves: [String] = []
    private var _fields: [[String]] = []
    let vocabularyValue: TaskListVocabulary
    let info: BoardInfo
    let drop: [UUID: UUID]

    init(rows: [TaskRow], vocabulary: TaskListVocabulary, info: BoardInfo, drop: [UUID: UUID]) {
        _rows = rows
        vocabularyValue = vocabulary
        self.info = info
        self.drop = drop
    }

    var rows: [TaskRow] {
        get { lock.withLock { _rows } }
        set { lock.withLock { _rows = newValue } }
    }

    var failure: TaskerAPIError? {
        get { lock.withLock { _failure } }
        set { lock.withLock { _failure = newValue } }
    }

    var moves: [String] { lock.withLock { _moves } }
    var fields: [[String]] { lock.withLock { _fields } }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { vocabularyValue }

    func board(project: UUID, id: UUID) async throws -> BoardInfo? { id == info.id ? info : nil }

    func columnPage(project: UUID, board: UUID, column: UUID, field: [String], offset: Int, limit: Int) async throws -> BoardRowPage {
        lock.withLock { _fields.append(field) }
        let statuses = info.columns.first { $0.id == column }?.statusIds ?? []
        let all = rows.filter { statuses.contains($0.statusId) }
        return BoardRowPage(rows: Array(all.dropFirst(offset).prefix(limit)), total: all.count)
    }

    func taskPage(project: UUID, typeIds: [UUID], offset: Int, limit: Int) async throws -> BoardRowPage {
        let all = rows.filter { typeIds.contains($0.typeId) }
        return BoardRowPage(rows: Array(all.dropFirst(offset).prefix(limit)), total: all.count)
    }

    func move(project: UUID, board: UUID, column: UUID, task: UUID, version: String) async throws -> UUID {
        lock.withLock { _moves.append("\(task.uuidString.prefix(4)) \(version)") }
        if let failure { throw failure }
        guard let status = info.columns.first(where: { $0.id == column })?.statusIds.first else { throw TaskerAPIError.notFound(Refusal(message: "")) }
        rows = rows.map { row in
            guard row.id == task else { return row }
            var changed = row
            changed.statusId = status
            changed.version = version + "+"
            return changed
        }
        return status
    }
}

@MainActor
struct BoardViewTests {
    private let ru = Localizer(language: .ru)
    private let project = UUID()
    private let todo = UUID()
    private let doing = UUID()
    private let done = UUID()
    private let archived = UUID()
    private let set = UUID()
    private let type = UUID()
    private let series = UUID()
    private let board = UUID()
    private let todoColumn = UUID()
    private let doingColumn = UUID()
    private let priority = UUID()
    private let priorities = UUID()
    private let high = UUID()

    private var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = [
            .init(id: todo, name: "Туду", color: "#2684ff"),
            .init(id: doing, name: "В работе", color: "#f5a623"),
            .init(id: done, name: "Готово", color: "#36b37e"),
            .init(id: archived, name: "Архив", color: "#8993a4")
        ]
        value.statusSets = [set: [todo, doing, done, archived]]
        value.types = [type: .init(name: "Задача", statusSetId: set)]
        value.typeOrder = [type]
        value.prefixes = [series: "TSK"]
        value.boardNames = [board: "Основная"]
        value.boardOrder = [board]
        value.fields = [.init(id: priority, name: "Приоритет", type: .enum, multiple: false, enumId: priorities)]
        value.enums = [priorities: [.init(id: high.uuidString, name: "Высокий")]]
        return value
    }

    private var info: BoardInfo {
        BoardInfo(id: board, name: "Основная", setIds: [set], columns: [
            BoardColumnInfo(id: todoColumn, name: "Туду", statusIds: [todo], conditions: []),
            BoardColumnInfo(id: doingColumn, name: "В работе", statusIds: [doing], conditions: [BoardCondition(fieldId: priority, op: .equal, value: high.uuidString)])
        ])
    }

    private func row(_ number: Int, status: UUID) -> TaskRow {
        TaskRow(
            id: UUID(uuidString: String(format: "%08d-0000-0000-0000-000000000000", number))!,
            title: "Задача \(number)", typeId: type, statusId: status,
            seriesNumbers: [TaskSeriesRef(seriesId: series, number: number)],
            createdAt: Date(timeIntervalSince1970: 1_800_000_000), updatedAt: Date(timeIntervalSince1970: 1_800_000_000),
            version: "v\(number)", parentIds: [], childCount: 0, fields: [])
    }

    private func source(_ rows: [TaskRow]) -> StubBoardViewSource {
        StubBoardViewSource(rows: rows, vocabulary: vocabulary, info: info, drop: [:])
    }

    private func model(_ source: StubBoardViewSource) -> BoardViewModel {
        let model = BoardViewModel(project: project, board: board, source: source)
        model.localizer = ru
        return model
    }

    private func until(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<600 {
            if condition() { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    @Test func columnsShowFiftyCardsAndTheTotal() async {
        let stub = source((1...120).map { row($0, status: todo) } + [row(500, status: doing)])
        let model = model(stub)
        model.start()
        await until { model.phase == .ready }
        #expect(model.columns.map(\.rows.count) == [50, 1])
        #expect(model.columns.map(\.total) == [120, 1])
        #expect(model.columns[0].hidden == 70)
        #expect(model.shown == 51)
    }

    @Test func tasksOutsideColumnsAreCounted() async {
        let stub = source([row(1, status: todo), row(2, status: done), row(3, status: archived)])
        let model = model(stub)
        model.start()
        await until { model.phase == .ready }
        #expect(model.outside == 2)
        let outside = await model.outsideTasks()
        #expect(Set(outside.map(\.title)) == ["Задача 2", "Задача 3"])
    }

    @Test func showAllLoadsTheRestOfTheColumn() async {
        let stub = source((1...260).map { row($0, status: todo) })
        let model = model(stub)
        model.start()
        await until { model.phase == .ready }
        model.showAll(todoColumn)
        await until { model.columns[0].rows.count == 260 }
        #expect(model.columns[0].rows.count == 260)
        #expect(model.columns[0].hidden == 0)
        stub.rows += [row(900, status: todo)]
        model.scheduleRefresh()
        await until { model.columns[0].rows.count == 261 }
        #expect(model.columns[0].rows.count == 261)
    }

    @Test func movingACardAsksTheServerAndRelocatesIt() async {
        let first = row(1, status: todo)
        let stub = source([first])
        let model = model(stub)
        var notices: [String] = []
        model.onNotice = { notices.append($0) }
        model.start()
        await until { model.phase == .ready }
        model.move(task: first.id, to: doingColumn)
        #expect(model.moving[first.id] == doingColumn)
        await until { model.columns[1].rows.count == 1 }
        #expect(stub.moves == ["0000 v1"])
        #expect(model.columns.map(\.rows.count) == [0, 1])
        #expect(model.moving.isEmpty)
        #expect(notices == ["TSK-1 → «В работе»"])
    }

    @Test func aCardCannotBeDroppedOnItsOwnColumnOrWhileMoving() async {
        let first = row(1, status: todo)
        let model = model(source([first]))
        model.start()
        await until { model.phase == .ready }
        #expect(!model.canDrop(task: first.id, on: todoColumn))
        #expect(model.canDrop(task: first.id, on: doingColumn))
        model.move(task: first.id, to: doingColumn)
        #expect(!model.canDrop(task: first.id, on: doingColumn))
    }

    @Test func aRefusalKeepsTheCardAndShowsTheServerMessage() async {
        let first = row(1, status: todo)
        let stub = source([first])
        stub.failure = .badRequest(Refusal(
            code: "board.taskNotMatchColumn", message: "raw",
            params: ["task": .string("Задача 1"), "column": .string("В работе"), "conditions": .array([.string("Приоритет=Высокий")])]))
        let model = model(stub)
        var failures: [String] = []
        model.onFailure = { failures.append($0) }
        model.start()
        await until { model.phase == .ready }
        model.move(task: first.id, to: doingColumn)
        await until { !failures.isEmpty }
        #expect(failures == [ru.string(.errorBoardTaskNotMatchColumn, "Задача 1", "В работе", "Приоритет=Высокий")])
        #expect(model.moving.isEmpty)
        #expect(model.refusal == BoardRefusal(column: doingColumn, tick: 1))
        #expect(model.columns.map(\.rows.count) == [1, 0])
    }

    @Test func aFieldChangeMovesTheCardOnRefresh() async {
        var first = row(1, status: todo)
        let stub = source([first])
        let model = model(stub)
        model.start()
        await until { model.phase == .ready }
        first.statusId = doing
        stub.rows = [first]
        model.scheduleRefresh()
        await until { model.columns[1].rows.count == 1 }
        #expect(model.columns.map(\.rows.count) == [0, 1])
    }

    @Test func conditionsReadLikeInTheMockup() async {
        let model = model(source([]))
        model.start()
        await until { model.phase == .ready }
        #expect(model.conditionText(BoardCondition(fieldId: priority, op: .equal, value: high.uuidString)) == "Приоритет = Высокий")
        #expect(model.conditionText(BoardCondition(fieldId: priority, op: .notEqual, value: high.uuidString)) == "Приоритет ≠ Высокий")
        #expect(model.conditionText(BoardCondition(fieldId: priority, op: .unset, value: "")) == "Приоритет не задано")
    }

    @Test func theQueryHookFiltersEveryColumn() async {
        let stub = source([row(1, status: todo)])
        let model = model(stub)
        model.start()
        await until { model.phase == .ready }
        model.fieldFilter = ["Приоритет=Высокий"]
        await until { stub.fields.contains { $0 == ["Приоритет=Высокий"] } }
        #expect(stub.fields.contains { $0 == ["Приоритет=Высокий"] })
        await until { model.phase == .ready }
        #expect(model.outside == 0)
    }

    @Test func aMissingBoardFallsBackToTheFirstOne() async {
        let model = BoardViewModel(project: project, board: UUID(), source: source([]))
        model.localizer = ru
        model.start()
        await until { model.phase == .ready }
        #expect(model.board?.id == board)
    }
}
