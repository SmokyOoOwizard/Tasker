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
    case ready(SeriesConflictPlan, stale: Bool)
    case applying
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
        loadPlan(stale: false)
    }

    private func loadPlan(stale: Bool) {
        Task { [weak self, source, project, series] in
            do {
                let plan = try await source.plan(project: project, series: series)
                guard let self else { return }
                if plan.isEmpty {
                    self.resolve = .idle
                    self.onNotice?(self.localizer.string(.seriesResolveEmpty))
                    self.load()
                } else {
                    self.resolve = .ready(plan, stale: stale)
                }
            } catch {
                guard let self else { return }
                self.resolve = .failed(self.localizer.string(.seriesResolvePlanFailed, Self.reason(error)))
            }
        }
    }

    func cancelResolve() {
        if case .applying = resolve { return }
        resolve = .idle
    }

    func applyResolve() {
        guard case .ready(let plan, _) = resolve else { return }
        resolve = .applying
        Task { [weak self, source, project, series] in
            do {
                try await source.resolve(project: project, series: series, token: plan.token)
            } catch {
                guard let self else { return }
                if case TaskerAPIError.conflict(let conflict) = error, conflict.kind == .modified {
                    self.load()
                    self.loadPlan(stale: true)
                } else {
                    self.resolve = .failed(self.localizer.string(.seriesResolveFailed, Self.reason(error)))
                }
                return
            }
            guard let self else { return }
            self.resolve = .idle
            self.onNotice?(self.localizer.string(.seriesResolveDone, plan.steps.count))
            self.onRenumbered?()
            self.load()
        }
    }

    private static func reason(_ error: any Error) -> String {
        (error as? TaskerAPIError)?.message ?? error.localizedDescription
    }
}
