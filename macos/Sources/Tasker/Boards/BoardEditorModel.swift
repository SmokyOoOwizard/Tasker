import Foundation
import Observation
import TaskerAPI

enum BoardEditorPhase: Equatable {
    case loading
    case ready
    case missing
    case failed(String)
}

struct BoardEditorTarget: Equatable {
    var project: UUID
    var board: UUID?
}

@MainActor
@Observable
final class BoardEditorModel {
    static let heartbeat: Duration = .seconds(40)

    let target: BoardEditorTarget

    private(set) var phase: BoardEditorPhase = .loading
    private(set) var catalog = BoardCatalog()
    private(set) var record: BoardRecord?
    private(set) var lock: BoardLockState = .none
    private(set) var saving = false
    private(set) var issues = BoardIssues()
    private(set) var staleVersion = false
    var draft = BoardDraft()

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onCreated: (() -> Void)?
    @ObservationIgnored private let source: any BoardEditorSource
    @ObservationIgnored private let heartbeat: Duration
    @ObservationIgnored private var initial = BoardDraft()
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var beatTask: Task<Void, Never>?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var released = false

    init(target: BoardEditorTarget, source: any BoardEditorSource, heartbeat: Duration = BoardEditorModel.heartbeat) {
        self.target = target
        self.source = source
        self.heartbeat = heartbeat
    }

    var isNew: Bool { target.board == nil }

    var isDirty: Bool {
        guard phase == .ready else { return false }
        return draft != (record?.draft ?? initial)
    }

    var holder: String? {
        if case .other(let name) = lock { return name }
        return nil
    }

    var canSave: Bool { phase == .ready && !saving && holder == nil }

    func load() {
        generation += 1
        let current = generation
        loadTask?.cancel()
        loadTask = Task { [weak self] in await self?.fetch(current) }
    }

    private func fetch(_ current: Int) async {
        do {
            let catalog = try await source.catalog(project: target.project)
            var loaded: BoardRecord?
            if let id = target.board {
                guard let found = try await source.board(project: target.project, id: id) else {
                    guard current == generation else { return }
                    phase = .missing
                    return
                }
                loaded = found
            }
            guard current == generation else { return }
            self.catalog = catalog
            if let loaded {
                record = loaded
                draft = loaded.draft
                staleVersion = false
                issues = BoardIssues()
                phase = .ready
                await takeLock(id: loaded.id)
                startBeat()
            } else {
                initial = BoardEditing.initial(catalog)
                draft = initial
                phase = .ready
            }
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            phase = .failed(Self.reason(error))
        }
    }

    private func takeLock(id: UUID) async {
        guard !released else { return }
        do {
            try await source.acquire(project: target.project, id: id)
            if released {
                try? await source.release(project: target.project, id: id)
                return
            }
            lock = .mine
        } catch {
            if case TaskerAPIError.conflict(let conflict) = error, conflict.kind == .locked {
                lock = .other(conflict.heldBy?.name ?? conflict.message)
            }
        }
    }

    private func startBeat() {
        beatTask?.cancel()
        guard let id = target.board else { return }
        let interval = heartbeat
        beatTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                if Task.isCancelled { return }
                await self?.takeLock(id: id)
            }
        }
    }

    func refreshLock() {
        guard let id = target.board, phase == .ready, !released else { return }
        Task { [weak self, source, project = target.project] in
            guard let state = try? await source.lock(project: project, id: id) else { return }
            guard let self, !self.released else { return }
            switch state {
            case .other: self.lock = state
            case .mine: self.lock = .mine
            case .none:
                if self.holder != nil { await self.takeLock(id: id) } else { self.lock = .none }
            }
        }
    }

    func edit(_ change: (inout BoardDraft, BoardCatalog) -> Void) {
        change(&draft, catalog)
        issues = BoardIssues()
    }

    func clearIssues() {
        if !issues.isEmpty { issues = BoardIssues() }
    }

    func save() async -> Bool {
        guard canSave else { return false }
        let found = BoardValidation.check(draft, catalog: catalog, l: localizer)
        if !found.isEmpty {
            issues = found
            return false
        }
        saving = true
        defer { saving = false }
        let sent = draft
        do {
            if let record {
                try await source.update(project: target.project, record: record, draft: sent, catalog: catalog)
                onNotice?(localizer.string(.boardSaved))
                issues = BoardIssues()
                await reloadAfterSave()
            } else {
                _ = try await source.create(project: target.project, draft: sent, catalog: catalog)
                onNotice?(localizer.string(.boardCreated, sent.name.trimmingCharacters(in: .whitespaces)))
                onCreated?()
            }
            return true
        } catch {
            fail(error, draft: sent)
            return false
        }
    }

    private func reloadAfterSave() async {
        guard let id = target.board else { return }
        if let fresh = try? await source.board(project: target.project, id: id) {
            record = fresh
            draft = fresh.draft
        }
    }

    private func fail(_ error: any Error, draft sent: BoardDraft) {
        guard case TaskerAPIError.conflict(let conflict) = error else {
            let message = (error as? TaskerAPIError)?.message ?? error.localizedDescription
            if case TaskerAPIError.badRequest(let text) = error {
                issues = BoardServerIssue.map(text, draft: sent, catalog: catalog, l: localizer)
            } else {
                issues = BoardIssues(board: [localizer.string(.boardSaveFailed, message)])
            }
            return
        }
        switch conflict.kind {
        case .locked:
            lock = .other(conflict.heldBy?.name ?? conflict.message)
            issues = BoardIssues(board: [localizer.string(.boardSaveBlocked, conflict.heldBy?.name ?? conflict.message)])
        case .modified:
            staleVersion = true
            issues = BoardIssues(board: [localizer.string(.boardModified)])
        default:
            issues = BoardIssues(board: [localizer.string(.boardSaveFailed, conflict.message)])
        }
    }

    func reloadFromServer() {
        guard !isNew else { return }
        load()
    }

    func release() {
        guard let work = stopHolding() else { return }
        Task { await work() }
    }

    func releaseNow() async {
        guard let work = stopHolding() else { return }
        await work()
    }

    private func stopHolding() -> (@Sendable () async -> Void)? {
        guard !released else { return nil }
        released = true
        generation += 1
        loadTask?.cancel()
        beatTask?.cancel()
        loadTask = nil
        beatTask = nil
        guard let id = target.board, lock == .mine else { return nil }
        let source = source
        let project = target.project
        return { try? await source.release(project: project, id: id) }
    }

    private static func reason(_ error: any Error) -> String {
        (error as? TaskerAPIError)?.message ?? error.localizedDescription
    }
}
