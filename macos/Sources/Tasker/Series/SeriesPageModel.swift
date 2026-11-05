import Foundation
import Observation
import TaskerAPI

enum SeriesPhase: Equatable {
    case loading
    case ready
    case missing
    case failed(String)
}

enum SeriesResolveState: Equatable {
    case idle
    case preparing
    case ready(SeriesConflictPlan)
    case applying(done: Int, total: Int)
    case failed(String)
}

@MainActor
@Observable
final class SeriesPageModel {
    let project: UUID
    let series: UUID

    private(set) var phase: SeriesPhase = .loading
    private(set) var overview: SeriesOverview?
    private(set) var stats: SeriesStats?
    private(set) var resolve: SeriesResolveState = .idle
    private(set) var saving = false
    var editing = false

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onRenumbered: (() -> Void)?
    @ObservationIgnored private let source: any SeriesSource
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var generation = 0

    init(project: UUID, series: UUID, source: any SeriesSource) {
        self.project = project
        self.series = series
        self.source = source
    }

    var info: SeriesInfo? { overview?.info }
    var all: [SeriesBrief] { overview?.all ?? [] }
    var conflicts: [SeriesNumberConflict] { (overview?.conflicts ?? []).filter { $0.seriesId == series } }
    var conflictCount: Int { conflicts.count }
    var nextNumber: Int { (stats?.maxNumber ?? 0) + 1 }

    func load() {
        generation += 1
        let current = generation
        loadTask?.cancel()
        loadTask = Task { [weak self] in await self?.fetch(current) }
    }

    func stop() {
        generation += 1
        loadTask?.cancel()
    }

    private func fetch(_ current: Int) async {
        do {
            async let overview = source.overview(project: project, series: series)
            async let stats = source.stats(project: project, series: series)
            let (loadedOverview, loadedStats) = try await (overview, stats)
            guard current == generation else { return }
            guard let loadedOverview else {
                phase = .missing
                return
            }
            self.overview = loadedOverview
            self.stats = loadedStats
            phase = .ready
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            if overview == nil { phase = .failed(error.localizedDescription) }
        }
    }

    func validate(name: String, prefix: String) -> String? {
        let others = all.filter { $0.id != series }.map(\.prefix)
        switch SeriesForm.validate(name: name, prefix: prefix, others: others) {
        case .none: return nil
        case .nameRequired: return localizer.string(.seriesNameRequired)
        case .prefixInvalid: return localizer.string(.seriesPrefixInvalid)
        case .prefixTaken(let value): return localizer.string(.seriesPrefixTaken, value)
        }
    }

    func save(name: String, prefix: String) async -> String? {
        guard let info, !saving else { return nil }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if let problem = validate(name: name, prefix: prefix) { return problem }
        if trimmed == info.name, prefix == info.prefix { return nil }
        saving = true
        defer { saving = false }
        do {
            try await source.update(project: project, series: series, name: trimmed, prefix: prefix, version: info.version)
        } catch {
            return Self.saveFailure(error, l: localizer)
        }
        onNotice?(localizer.string(.seriesSaved))
        load()
        return nil
    }

    static func saveFailure(_ error: any Error, l: Localizer) -> String {
        guard let error = error as? TaskerAPIError else { return l.string(.seriesSaveFailed, error.localizedDescription) }
        if case .conflict(let conflict) = error {
            switch conflict.kind {
            case .modified: return l.string(.seriesSaveModified)
            case .locked: return l.string(.seriesSaveLocked, conflict.heldBy?.name ?? conflict.message)
            default: break
            }
        }
        return l.string(.seriesSaveFailed, error.message)
    }

    func prepareResolve() {
        guard conflictCount > 0 else {
            onNotice?(localizer.string(.seriesResolveEmpty))
            return
        }
        resolve = .preparing
        let conflicts = conflicts
        let maxNumber = stats?.maxNumber ?? 0
        Task { [weak self, source, project] in
            do {
                var groups: [(number: Int, tasks: [SeriesConflictTask])] = []
                for conflict in conflicts {
                    var tasks: [SeriesConflictTask] = []
                    for id in conflict.taskIds {
                        if let task = try await source.conflictTask(project: project, task: id) { tasks.append(task) }
                    }
                    groups.append((conflict.number, tasks))
                }
                let plan = SeriesConflictPlanner.plan(conflicts: groups, maxNumber: maxNumber)
                guard let self else { return }
                self.resolve = plan.isEmpty ? .idle : .ready(plan)
                if plan.isEmpty { self.onNotice?(self.localizer.string(.seriesResolveEmpty)) }
            } catch {
                guard let self else { return }
                self.resolve = .failed(self.localizer.string(.seriesResolvePlanFailed, error.localizedDescription))
            }
        }
    }

    func cancelResolve() {
        if case .applying = resolve { return }
        resolve = .idle
    }

    func applyResolve() {
        guard case .ready(let plan) = resolve else { return }
        let steps = plan.steps
        resolve = .applying(done: 0, total: steps.count)
        Task { [weak self, source, project, series] in
            var done = 0
            for step in steps {
                do {
                    let fresh = try await source.conflictTask(project: project, task: step.task.id)
                    try await source.renumber(project: project, task: step.task.id, series: series, to: step.to, version: fresh?.version ?? step.task.version)
                } catch {
                    guard let self else { return }
                    self.resolve = .failed(self.localizer.string(
                        .seriesResolveFailed, step.task.title, Self.reason(error),
                        self.localizer.number(Double(done)), self.localizer.number(Double(steps.count))))
                    self.finishRenumbering(done)
                    return
                }
                done += 1
                self?.resolve = .applying(done: done, total: steps.count)
            }
            guard let self else { return }
            self.resolve = .idle
            self.onNotice?(self.localizer.string(.seriesResolveDone, done))
            self.finishRenumbering(done)
        }
    }

    private func finishRenumbering(_ done: Int) {
        guard done > 0 else { return }
        onRenumbered?()
        load()
    }

    private static func reason(_ error: any Error) -> String {
        (error as? TaskerAPIError)?.message ?? error.localizedDescription
    }
}
