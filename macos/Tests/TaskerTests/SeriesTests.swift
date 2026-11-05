import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubSeriesSource: SeriesSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _resolved: [String] = []
    private var _updates: [(name: String, prefix: String, version: String)] = []
    private var _resolveFailures: [TaskerAPIError] = []
    private var _updateFailure: TaskerAPIError?
    var overviewValue: SeriesOverview?
    var statsValue = SeriesStats(count: 0, maxNumber: 0)
    var plans: [SeriesConflictPlan] = []

    var resolved: [String] { lock.withLock { _resolved } }
    var updates: [(name: String, prefix: String, version: String)] { lock.withLock { _updates } }
    func failResolve(_ error: TaskerAPIError) { lock.withLock { _resolveFailures.append(error) } }
    func failUpdate(_ error: TaskerAPIError) { lock.withLock { _updateFailure = error } }

    func overview(project: UUID, series: UUID) async throws -> SeriesOverview? { overviewValue }
    func stats(project: UUID, series: UUID) async throws -> SeriesStats { statsValue }

    func plan(project: UUID, series: UUID) async throws -> SeriesConflictPlan {
        lock.withLock { plans.count > 1 ? plans.removeFirst() : plans[0] }
    }

    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws {
        try lock.withLock {
            if let failure = _updateFailure { throw failure }
            _updates.append((name, prefix, version))
        }
    }

    func resolve(project: UUID, series: UUID, token: String) async throws {
        try lock.withLock {
            if !_resolveFailures.isEmpty { throw _resolveFailures.removeFirst() }
            _resolved.append(token)
        }
    }
}

@MainActor
struct SeriesTests {
    let project = UUID()
    let series = UUID()
    let other = UUID()
    let ru = Localizer(choice: .ru)

    private func step(_ index: Int, from: Int = 133, to: Int, keeper: Int = 1) -> SeriesPlanStep {
        SeriesPlanStep(
            taskId: UUID(uuidString: String(format: "%08d-0000-0000-0000-000000000000", index))!, title: "Задача \(index)", from: from, to: to,
            keeperId: UUID(uuidString: String(format: "%08d-0000-0000-0000-000000000000", keeper))!, keeperTitle: "Задача \(keeper)")
    }

    private func overview(conflicts: [SeriesNumberConflict] = [], prefix: String = "TSK") -> SeriesOverview {
        SeriesOverview(
            info: SeriesInfo(id: series, name: "Основная", prefix: prefix, version: "sv"),
            all: [SeriesBrief(id: series, name: "Основная", prefix: prefix), SeriesBrief(id: other, name: "Релизы", prefix: "REL")],
            conflicts: conflicts)
    }

    private func until(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<400 {
            if condition() { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    @Test func planGroupsStepsByNumberKeepingServerOrder() {
        let plan = SeriesConflictPlan(token: "t", steps: [step(2, from: 2, to: 11, keeper: 1), step(4, from: 5, to: 12, keeper: 3), step(5, from: 5, to: 13, keeper: 3)])
        #expect(plan.groups.map(\.number) == [2, 5])
        #expect(plan.groups[1].moves.map(\.to) == [12, 13])
        #expect(plan.groups[1].keeperTitle == "Задача 3")
        #expect(!plan.isEmpty)
        #expect(SeriesConflictPlan(token: "t", steps: []).isEmpty)
    }

    @Test func formValidationFollowsTheServerRules() {
        #expect(SeriesForm.validate(name: "  ", prefix: "TSK", others: []) == .nameRequired)
        #expect(SeriesForm.validate(name: "A", prefix: "", others: []) == .prefixInvalid)
        #expect(SeriesForm.validate(name: "A", prefix: "T-K", others: []) == .prefixInvalid)
        #expect(SeriesForm.validate(name: "A", prefix: "ТСК", others: []) == .prefixInvalid)
        #expect(SeriesForm.validate(name: "A", prefix: String(repeating: "A", count: 21), others: []) == .prefixInvalid)
        #expect(SeriesForm.validate(name: "A", prefix: String(repeating: "A", count: 20), others: []) == nil)
        #expect(SeriesForm.validate(name: "A", prefix: "REL", others: ["REL"]) == .prefixTaken("REL"))
        #expect(SeriesForm.validate(name: "A", prefix: "rel", others: ["REL"]) == nil)
    }

    @Test func referencePrefersTheViewedSeries() {
        var vocabulary = TaskListVocabulary()
        vocabulary.prefixes = [series: "TSK", other: "REL"]
        let row = TaskRow(
            id: UUID(), title: "t", typeId: UUID(), statusId: UUID(),
            seriesNumbers: [TaskSeriesRef(seriesId: series, number: 121), TaskSeriesRef(seriesId: other, number: 3)],
            createdAt: .now, updatedAt: .now, version: "v", parentIds: [], childCount: 0, fields: [])
        #expect(vocabulary.reference(row) == "TSK-121")
        #expect(vocabulary.reference(row, preferring: other) == "REL-3")
        #expect(vocabulary.reference(row, preferring: UUID()) == "TSK-121")
    }

    @Test func modelLoadsOnlyConflictsOfItsOwnSeries() async {
        let source = StubSeriesSource()
        source.overviewValue = overview(conflicts: [
            SeriesNumberConflict(seriesId: series, number: 133, taskIds: [UUID(), UUID()]),
            SeriesNumberConflict(seriesId: other, number: 1, taskIds: [UUID(), UUID()])
        ])
        source.statsValue = SeriesStats(count: 25, maxNumber: 143)
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.load()
        await until { model.phase == .ready }
        #expect(model.conflictCount == 1)
        #expect(model.nextNumber == 144)
        #expect(model.stats?.count == 25)
        #expect(model.all.map(\.prefix) == ["TSK", "REL"])
    }

    @Test func missingSeriesIsReported() async {
        let source = StubSeriesSource()
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.load()
        await until { model.phase == .missing }
        #expect(model.phase == .missing)
    }

    private func readyModel(_ source: StubSeriesSource) async -> SeriesPageModel {
        source.overviewValue = overview(conflicts: [SeriesNumberConflict(seriesId: series, number: 133, taskIds: [UUID(), UUID()])])
        source.statsValue = SeriesStats(count: 2, maxNumber: 140)
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        model.load()
        await until { model.phase == .ready }
        return model
    }

    @Test func resolvingShowsTheServerPlanFirstAndConfirmsItByToken() async {
        let source = StubSeriesSource()
        source.plans = [SeriesConflictPlan(token: "abc", steps: [step(2, to: 141)])]
        let model = await readyModel(source)
        var notes: [String] = []
        model.onNotice = { notes.append($0) }
        var renumbered = 0
        model.onRenumbered = { renumbered += 1 }

        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        guard case .ready(let plan, let stale) = model.resolve else {
            Issue.record("plan was not prepared")
            return
        }
        #expect(plan.token == "abc")
        #expect(!stale)
        #expect(source.resolved.isEmpty)

        model.applyResolve()
        await until { model.resolve == .idle }
        #expect(source.resolved == ["abc"])
        #expect(renumbered == 1)
        #expect(notes == [ru.string(.seriesResolveDone, 1)])
    }

    @Test func cancellingThePlanChangesNothing() async {
        let source = StubSeriesSource()
        source.plans = [SeriesConflictPlan(token: "abc", steps: [step(2, to: 141)])]
        let model = await readyModel(source)
        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        model.cancelResolve()
        #expect(model.resolve == .idle)
        #expect(source.resolved.isEmpty)
    }

    @Test func aStalePlanIsReplacedByTheFreshOneAndNeedsANewConfirmation() async {
        let source = StubSeriesSource()
        source.plans = [
            SeriesConflictPlan(token: "old", steps: [step(2, to: 141)]),
            SeriesConflictPlan(token: "new", steps: [step(2, to: 142)])
        ]
        source.failResolve(TaskerAPIError.modified())
        let model = await readyModel(source)
        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }

        model.applyResolve()
        await until { if case .ready(let plan, true) = model.resolve { plan.token == "new" } else { false } }
        guard case .ready(let plan, let stale) = model.resolve else {
            Issue.record("expected a refreshed plan")
            return
        }
        #expect(plan.token == "new")
        #expect(stale)
        #expect(source.resolved.isEmpty)

        model.applyResolve()
        await until { model.resolve == .idle }
        #expect(source.resolved == ["new"])
    }

    @Test func otherFailuresAreShownAndNothingIsRetried() async {
        let source = StubSeriesSource()
        source.plans = [SeriesConflictPlan(token: "abc", steps: [step(2, to: 141)])]
        source.failResolve(TaskerAPIError.locked(by: "Аня"))
        let model = await readyModel(source)
        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        model.applyResolve()
        await until { if case .failed = model.resolve { true } else { false } }
        guard case .failed(let message) = model.resolve else {
            Issue.record("expected a failure")
            return
        }
        #expect(message.contains("Аня"))
        #expect(source.resolved.isEmpty)
    }

    @Test func resolvingWithoutConflictsOnlyTellsSo() async {
        let source = StubSeriesSource()
        source.overviewValue = overview()
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        var notes: [String] = []
        model.onNotice = { notes.append($0) }
        model.load()
        await until { model.phase == .ready }
        model.prepareResolve()
        #expect(model.resolve == .idle)
        #expect(notes == [ru.string(.seriesResolveEmpty)])
    }

    @Test func savingSendsTheVersionAndSkipsUnchangedValues() async {
        let source = StubSeriesSource()
        source.overviewValue = overview()
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        model.load()
        await until { model.phase == .ready }
        #expect(await model.save(name: "Основная", prefix: "TSK") == nil)
        #expect(source.updates.isEmpty)
        #expect(await model.save(name: " Новое ", prefix: "NEW") == nil)
        #expect(source.updates.count == 1)
        #expect(source.updates[0].name == "Новое")
        #expect(source.updates[0].prefix == "NEW")
        #expect(source.updates[0].version == "sv")
    }

    @Test func savingRejectsATakenPrefixWithoutCallingTheServer() async {
        let source = StubSeriesSource()
        source.overviewValue = overview()
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        model.load()
        await until { model.phase == .ready }
        #expect(await model.save(name: "Основная", prefix: "REL") == ru.string(.seriesPrefixTaken, "REL"))
        #expect(source.updates.isEmpty)
    }

    @Test func savingExplainsVersionAndLockConflicts() async {
        let source = StubSeriesSource()
        source.overviewValue = overview()
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        model.load()
        await until { model.phase == .ready }
        source.failUpdate(TaskerAPIError.modified())
        #expect(await model.save(name: "Новое", prefix: "TSK") == ru.string(.seriesSaveModified))
        source.failUpdate(TaskerAPIError.locked(by: "Аня"))
        #expect(await model.save(name: "Новое", prefix: "TSK")?.contains("Аня") == true)
    }

    @Test func sessionShowsAndClosesTheSeriesPage() {
        let defaults = UserDefaults(suiteName: "SeriesTests-" + UUID().uuidString)!
        let layout = WorkspaceLayout(defaults: defaults)
        let session = WorkspaceTabSession(tab: nil, layout: layout, live: LiveWorkspace(), defaults: defaults)
        session.showSeries(series)
        #expect(session.seriesPage == series)
        #expect(session.section == .tasks)
        session.closeSeries()
        #expect(session.seriesPage == nil)
    }
}
