import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubSeriesSource: SeriesSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _renumbered: [(task: UUID, to: Int, version: String)] = []
    private var _updates: [(name: String, prefix: String, version: String)] = []
    private var _failTask: UUID?
    private var _updateFailure: TaskerAPIError?
    var overviewValue: SeriesOverview?
    var statsValue = SeriesStats(count: 0, maxNumber: 0)
    var tasks: [UUID: SeriesConflictTask] = [:]

    var renumbered: [(task: UUID, to: Int, version: String)] { lock.withLock { _renumbered } }
    var updates: [(name: String, prefix: String, version: String)] { lock.withLock { _updates } }
    func failRenumber(of task: UUID) { lock.withLock { _failTask = task } }
    func failUpdate(_ error: TaskerAPIError) { lock.withLock { _updateFailure = error } }

    func overview(project: UUID, series: UUID) async throws -> SeriesOverview? { overviewValue }
    func stats(project: UUID, series: UUID) async throws -> SeriesStats { statsValue }
    func conflictTask(project: UUID, task: UUID) async throws -> SeriesConflictTask? { tasks[task] }

    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws {
        try lock.withLock {
            if let failure = _updateFailure { throw failure }
            _updates.append((name, prefix, version))
        }
    }

    func renumber(project: UUID, task: UUID, series: UUID, to number: Int, version: String) async throws {
        try lock.withLock {
            if _failTask == task { throw TaskerAPIError.conflict(Conflict(kind: .locked, message: "locked")) }
            _renumbered.append((task, number, version))
        }
    }
}

@MainActor
struct SeriesTests {
    let project = UUID()
    let series = UUID()
    let other = UUID()
    let ru = Localizer(choice: .ru)

    private func task(_ index: Int, created: TimeInterval, number: Int = 133) -> SeriesConflictTask {
        SeriesConflictTask(
            id: UUID(uuidString: String(format: "%08d-0000-0000-0000-000000000000", index))!, title: "Задача \(index)",
            createdAt: Date(timeIntervalSince1970: 1_800_000_000 + created), version: "v\(index)",
            numbers: [TaskSeriesRef(seriesId: series, number: number)])
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

    @Test func earliestTaskKeepsTheNumberAndOthersGetNextFreeOnes() {
        let early = task(2, created: 10)
        let middle = task(1, created: 20)
        let late = task(3, created: 30)
        let plan = SeriesConflictPlanner.plan(conflicts: [(133, [late, early, middle])], maxNumber: 143)
        #expect(plan.groups.count == 1)
        #expect(plan.groups[0].keeper.id == early.id)
        #expect(plan.steps.map(\.task.id) == [middle.id, late.id])
        #expect(plan.steps.map(\.to) == [144, 145])
        #expect(plan.steps.map(\.from) == [133, 133])
    }

    @Test func equalCreationDatesFallBackToIdentifierOrder() {
        let first = task(1, created: 5)
        let second = task(2, created: 5)
        let plan = SeriesConflictPlanner.plan(conflicts: [(7, [second, first])], maxNumber: 9)
        #expect(plan.groups[0].keeper.id == first.id)
        #expect(plan.steps.map(\.to) == [10])
    }

    @Test func numbersContinueAcrossConflictsInAscendingOrder() {
        let a = task(1, created: 1, number: 5)
        let b = task(2, created: 2, number: 5)
        let c = task(3, created: 3, number: 2)
        let d = task(4, created: 4, number: 2)
        let plan = SeriesConflictPlanner.plan(conflicts: [(5, [a, b]), (2, [c, d])], maxNumber: 10)
        #expect(plan.groups.map(\.number) == [2, 5])
        #expect(plan.steps.map(\.to) == [11, 12])
        #expect(plan.steps.map(\.task.id) == [d.id, b.id])
    }

    @Test func conflictWithASingleTaskProducesNoSteps() {
        let plan = SeriesConflictPlanner.plan(conflicts: [(1, [task(1, created: 1)])], maxNumber: 1)
        #expect(plan.isEmpty)
        #expect(plan.steps.isEmpty)
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

    @Test func resolvingShowsAPlanFirstAndAppliesItInOrder() async {
        let early = task(1, created: 1)
        let late = task(2, created: 2)
        let source = StubSeriesSource()
        source.overviewValue = overview(conflicts: [SeriesNumberConflict(seriesId: series, number: 133, taskIds: [late.id, early.id])])
        source.statsValue = SeriesStats(count: 2, maxNumber: 140)
        source.tasks = [early.id: early, late.id: late]
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        var notes: [String] = []
        model.onNotice = { notes.append($0) }
        var renumbered = 0
        model.onRenumbered = { renumbered += 1 }
        model.load()
        await until { model.phase == .ready }

        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        guard case .ready(let plan) = model.resolve else {
            Issue.record("plan was not prepared")
            return
        }
        #expect(plan.steps.map(\.task.id) == [late.id])
        #expect(source.renumbered.isEmpty)

        model.applyResolve()
        await until { model.resolve == .idle }
        #expect(source.renumbered.map(\.task) == [late.id])
        #expect(source.renumbered.map(\.to) == [141])
        #expect(source.renumbered.map(\.version) == ["v2"])
        #expect(renumbered == 1)
        #expect(notes == [ru.string(.seriesResolveDone, 1)])
    }

    @Test func cancellingThePlanChangesNothing() async {
        let early = task(1, created: 1)
        let late = task(2, created: 2)
        let source = StubSeriesSource()
        source.overviewValue = overview(conflicts: [SeriesNumberConflict(seriesId: series, number: 133, taskIds: [early.id, late.id])])
        source.tasks = [early.id: early, late.id: late]
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.load()
        await until { model.phase == .ready }
        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        model.cancelResolve()
        #expect(model.resolve == .idle)
        #expect(source.renumbered.isEmpty)
    }

    @Test func resolvingStopsAtTheFirstFailureAndReportsProgress() async {
        let a = task(1, created: 1)
        let b = task(2, created: 2)
        let c = task(3, created: 3)
        let source = StubSeriesSource()
        source.overviewValue = overview(conflicts: [SeriesNumberConflict(seriesId: series, number: 133, taskIds: [a.id, b.id, c.id])])
        source.statsValue = SeriesStats(count: 3, maxNumber: 133)
        source.tasks = [a.id: a, b.id: b, c.id: c]
        source.failRenumber(of: c.id)
        let model = SeriesPageModel(project: project, series: series, source: source)
        model.localizer = ru
        model.load()
        await until { model.phase == .ready }
        model.prepareResolve()
        await until { if case .ready = model.resolve { true } else { false } }
        model.applyResolve()
        await until { if case .failed = model.resolve { true } else { false } }
        guard case .failed(let message) = model.resolve else {
            Issue.record("expected a failure")
            return
        }
        #expect(message.contains("Задача 3"))
        #expect(message.contains("1 из 2"))
        #expect(source.renumbered.map(\.task) == [b.id])
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
        source.failUpdate(.conflict(Conflict(kind: .modified, message: "x")))
        #expect(await model.save(name: "Новое", prefix: "TSK") == ru.string(.seriesSaveModified))
        source.failUpdate(.conflict(Conflict(kind: .locked, message: "x", heldBy: LockHolder(name: "Аня", expiresAt: nil))))
        #expect(await model.save(name: "Новое", prefix: "TSK") == ru.string(.seriesSaveLocked, "Аня"))
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
