import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubTaskQuerySource: TaskQuerySource, @unchecked Sendable {
    private let lock = NSLock()
    private var _calls: [(String, Int)] = []
    private var _reports: [String: TaskQueryReport] = [:]

    var calls: [(String, Int)] { lock.withLock { _calls } }

    func set(_ text: String, _ report: TaskQueryReport) {
        lock.withLock { _reports[text] = report }
    }

    func analyze(project: UUID, text: String, cursor: Int) async throws -> TaskQueryReport {
        lock.withLock {
            _calls.append((text, cursor))
            return _reports[text] ?? TaskQueryReport(valid: true, hasSort: false, errors: [], hints: [])
        }
    }
}

@MainActor
struct TaskQueryModelTests {
    private func hint(_ label: String, insert: String, start: Int, end: Int, caret: Int? = nil) -> TaskQueryHint {
        TaskQueryHint(kind: .value, label: label, insert: insert, start: start, end: end, caret: caret ?? insert.count, detail: nil)
    }

    private func until(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<200 where !condition() {
            try? await Task.sleep(for: .milliseconds(10))
        }
    }

    private func model(_ source: StubTaskQuerySource, text: String = "") -> TaskQueryModel {
        TaskQueryModel(project: UUID(), source: source, text: text)
    }

    @Test func aValidTextIsAppliedAfterTheAnalysis() async {
        let source = StubTaskQuerySource()
        source.set(".Status == \"Todo\" | sort .Title", TaskQueryReport(valid: true, hasSort: true, errors: [], hints: []))
        let model = model(source)
        var applied: [(String, Bool)] = []
        model.onApply = { applied.append(($0, $1)) }
        model.edited(".Status == \"Todo\" | sort .Title", cursor: 5)
        await until { !applied.isEmpty }
        #expect(applied.last?.0 == ".Status == \"Todo\" | sort .Title")
        #expect(applied.last?.1 == true)
        #expect(model.hasSort)
    }

    @Test func anInvalidTextKeepsTheAppliedQueryAndShowsTheError() async {
        let source = StubTaskQuerySource()
        source.set(".Status == \"Nope\"", TaskQueryReport(valid: false, hasSort: false, errors: [
            TaskQueryIssue(code: "query.unknownStatus", message: "No status", params: ["name": "Nope"], start: 11, end: 17)
        ], hints: []))
        let model = model(source, text: ".Type == \"Bug\"")
        var applied: [String] = []
        model.onApply = { text, _ in applied.append(text) }
        model.edited(".Status == \"Nope\"", cursor: 17)
        await until { model.error != nil }
        #expect(model.error?.code == "query.unknownStatus")
        #expect(model.error?.refusal.params["name"]?.stringValue == "Nope")
        #expect(model.applied == ".Type == \"Bug\"")
        #expect(applied.isEmpty)
    }

    @Test func unfinishedInputWithHintsIsNotShownAsAnError() async {
        let source = StubTaskQuerySource()
        let incomplete = TaskQueryIssue(code: "query.expectedOperator", message: "", params: [:], start: 0, end: 3)
        source.set(".St", TaskQueryReport(valid: false, hasSort: false, errors: [incomplete], hints: [hint(".Status", insert: ".Status ", start: 0, end: 3)]))
        source.set(".Zz", TaskQueryReport(valid: false, hasSort: false, errors: [incomplete], hints: []))
        let unknown = TaskQueryIssue(code: "query.unknownStatus", message: "", params: [:], start: 11, end: 17)
        source.set(".Status == \"Nope\"", TaskQueryReport(valid: false, hasSort: false, errors: [unknown], hints: [hint("&&", insert: "&& ", start: 17, end: 17)]))
        let model = model(source)
        model.edited(".St", cursor: 3)
        await until { model.showsHints }
        #expect(model.error == nil)
        #expect(model.errors == [incomplete])
        model.close()
        #expect(model.error == incomplete)
        model.edited(".Zz", cursor: 3)
        await until { model.hints.isEmpty }
        #expect(model.error == incomplete)
        model.edited(".Status == \"Nope\"", cursor: 17)
        await until { model.showsHints }
        #expect(model.error == unknown)
    }

    @Test func oldAnswersDoNotReplaceNewOnes() async {
        let source = StubTaskQuerySource()
        source.set(".A", TaskQueryReport(valid: false, hasSort: false, errors: [TaskQueryIssue(code: "a", message: "", params: [:], start: 0, end: 1)], hints: []))
        let model = model(source)
        model.edited(".A", cursor: 2)
        model.edited(".B", cursor: 2)
        await until { source.calls.contains { $0.0 == ".B" } }
        try? await Task.sleep(for: .milliseconds(30))
        #expect(model.error == nil)
        #expect(!source.calls.contains { $0.0 == ".A" })
    }

    @Test func arrowsCycleAndTabInsertsTheSelectedHint() async {
        let source = StubTaskQuerySource()
        let text = ".Status == \"to"
        source.set(text, TaskQueryReport(valid: false, hasSort: false, errors: [], hints: [
            hint("Todo", insert: "\"Todo\" ", start: 11, end: 14),
            hint("Tomorrow", insert: "\"Tomorrow\" ", start: 11, end: 14)
        ]))
        let model = model(source)
        model.edited(text, cursor: 14)
        await until { model.showsHints }
        #expect(model.selected == nil)
        model.move(1)
        #expect(model.selected == 0)
        model.move(1)
        #expect(model.selected == 1)
        model.move(1)
        #expect(model.selected == 0)
        model.move(-1)
        #expect(model.selected == 1)
        #expect(model.accept())
        #expect(model.text == ".Status == \"Tomorrow\" ")
        #expect(model.cursor == 22)
        #expect(model.revision == 1)
    }

    @Test func tabBeforeTheAnswerWaitsForFreshHints() async {
        let source = StubTaskQuerySource()
        source.set(".St", TaskQueryReport(valid: false, hasSort: false, errors: [], hints: [hint(".Status", insert: ".Status ", start: 0, end: 3)]))
        let model = model(source)
        model.edited(".St", cursor: 3)
        #expect(model.accept())
        #expect(model.text == ".St")
        await until { model.text == ".Status " }
        #expect(model.text == ".Status ")
    }

    @Test func tabWithoutSelectionTakesTheFirstAndCaretLandsInsideQuotes() async {
        let source = StubTaskQuerySource()
        source.set(".Title.", TaskQueryReport(valid: false, hasSort: false, errors: [], hints: [
            hint("Contains(…)", insert: "Contains(\"\") ", start: 7, end: 7, caret: 10)
        ]))
        let model = model(source)
        model.edited(".Title.", cursor: 7)
        await until { model.showsHints }
        #expect(model.accept())
        #expect(model.text == ".Title.Contains(\"\") ")
        #expect(model.cursor == 17)
    }

    @Test func enterAppliesWithoutASelectionAndAcceptsWithOne() async {
        let source = StubTaskQuerySource()
        source.set(".S", TaskQueryReport(valid: false, hasSort: false, errors: [], hints: [hint(".Status", insert: ".Status ", start: 0, end: 2)]))
        let model = model(source)
        model.edited(".S", cursor: 2)
        await until { model.showsHints }
        model.submit()
        #expect(!model.open)
        #expect(model.text == ".S")

        model.edited(".S ", cursor: 3)
        model.edited(".S", cursor: 2)
        await until { model.showsHints }
        model.move(1)
        model.submit()
        await until { model.text == ".Status " }
        #expect(model.text == ".Status ")
    }

    @Test func escapeClosesAndDownReopens() async {
        let source = StubTaskQuerySource()
        source.set(".S", TaskQueryReport(valid: false, hasSort: false, errors: [], hints: [hint(".Status", insert: ".Status ", start: 0, end: 2)]))
        let model = model(source)
        model.edited(".S", cursor: 2)
        await until { model.showsHints }
        model.close()
        #expect(!model.showsHints)
        model.move(1)
        await until { model.showsHints }
        #expect(model.open)
    }

    @Test func movingTheCaretAsksForHintsAtTheNewPlace() async {
        let source = StubTaskQuerySource()
        let model = model(source)
        model.edited(".Status == ", cursor: 11)
        await until { source.calls.last?.1 == 11 }
        model.caretMoved(3)
        await until { source.calls.last?.1 == 3 }
        #expect(source.calls.last?.1 == 3)
    }

    @Test func clearingAppliesAnEmptyQuery() async {
        let source = StubTaskQuerySource()
        let model = model(source, text: ".Type == \"Bug\"")
        var applied: [String] = []
        var saved = 0
        model.onApply = { text, _ in applied.append(text) }
        model.onTextChange = { saved += 1 }
        model.clear()
        #expect(applied == [""])
        #expect(model.text.isEmpty)
        #expect(saved == 1)
    }

    @Test func aRestoredQueryKnowsItsSortWithoutOpeningHints() async {
        let source = StubTaskQuerySource()
        source.set("| sort .Title", TaskQueryReport(valid: true, hasSort: true, errors: [], hints: [hint("x", insert: "x", start: 0, end: 0)]))
        let model = model(source, text: "| sort .Title")
        #expect(model.applied == "| sort .Title")
        model.start()
        await until { model.hasSort }
        #expect(model.hasSort)
        #expect(!model.showsHints)
    }

    @Test func aRestoredInvalidQueryStopsFilteringButStaysInTheField() async {
        let source = StubTaskQuerySource()
        source.set(".Status == ", TaskQueryReport(valid: false, hasSort: false, errors: [
            TaskQueryIssue(code: "query.expectedValue", message: "", params: [:], start: 11, end: 11)
        ], hints: []))
        let model = model(source, text: ".Status == ")
        var applied: [String] = []
        model.onApply = { text, _ in applied.append(text) }
        model.start()
        await until { !applied.isEmpty }
        #expect(applied == [""])
        #expect(model.text == ".Status == ")
        #expect(model.error != nil)
    }

    @Test func theViewStateKeepsOldTabsReadable() throws {
        let old = #"{"history":[],"scrollY":0}"#.data(using: .utf8)!
        let state = try JSONDecoder().decode(WorkspaceTabViewState.self, from: old)
        #expect(state.listQuery == nil)
        var next = state
        next.listQuery = ".Type == \"Bug\""
        next.boardQueries = ["a": "| sort .Title"]
        let round = try JSONDecoder().decode(WorkspaceTabViewState.self, from: JSONEncoder().encode(next))
        #expect(round == next)
    }

    @Test func listRequestsCarryTheQuery() async {
        let id = UUID()
        let stub = StubTaskListSource(rows: [], vocabulary: .empty)
        let list = TaskListModel(project: id, source: stub, settings: nil)
        list.query = ".Type == \"Bug\""
        #expect(list.request.query == ".Type == \"Bug\"")
        list.query = ""
        #expect(list.request.query == nil)
    }
}
