import Foundation
import Observation
import TaskerAPI

enum EditLockState: Equatable, Sendable {
    case none
    case mine
    case other(String)
}

struct EditLockOps: Sendable {
    var acquire: @Sendable () async throws -> Void
    var release: @Sendable () async throws -> Void
    var probe: (@Sendable () async throws -> EditLockState)?
}

enum EditFailure: Equatable {
    case modified(Refusal)
    case locked(holder: String, refusal: Refusal)
    case other

    init(_ error: any Error) {
        if case TaskerAPIError.conflict(let conflict) = error {
            switch conflict.kind {
            case .modified:
                self = .modified(conflict.refusal)
                return
            case .locked:
                self = .locked(holder: conflict.heldBy?.name ?? conflict.message, refusal: conflict.refusal)
                return
            default:
                break
            }
        }
        self = .other
    }
}

@MainActor
@Observable
final class EditLock {
    static let heartbeat: Duration = .seconds(40)

    private(set) var state: EditLockState = .none
    private(set) var refusal: Refusal?

    @ObservationIgnored private let ops: EditLockOps
    @ObservationIgnored private let interval: Duration
    @ObservationIgnored private var beatTask: Task<Void, Never>?
    @ObservationIgnored private var released = false

    init(ops: EditLockOps, heartbeat: Duration = EditLock.heartbeat) {
        self.ops = ops
        self.interval = heartbeat
    }

    var holder: String? {
        if case .other(let name) = state { return name }
        return nil
    }

    var isMine: Bool { state == .mine }
    var isReleased: Bool { released }

    @discardableResult
    func acquire() async -> EditLockState {
        guard !released else { return state }
        do {
            try await ops.acquire()
            if released {
                try? await ops.release()
                return state
            }
            state = .mine
        } catch {
            if case .locked(let holder, let found) = EditFailure(error) {
                state = .other(holder)
                refusal = found
            }
        }
        return state
    }

    func start() {
        beatTask?.cancel()
        guard !released else { return }
        let interval = interval
        beatTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                if Task.isCancelled { return }
                guard let self else { return }
                await self.acquire()
            }
        }
    }

    func refresh() async {
        guard !released, let probe = ops.probe, let found = try? await probe(), !released else { return }
        switch found {
        case .other, .mine:
            state = found
        case .none:
            if holder != nil { await acquire() } else { state = .none }
        }
    }

    func markLocked(by holder: String) {
        guard !released else { return }
        state = .other(holder)
    }

    func relinquish() async {
        beatTask?.cancel()
        beatTask = nil
        guard state == .mine else {
            return
        }
        state = .none
        try? await ops.release()
    }

    func stop() -> (@Sendable () async -> Void)? {
        guard !released else { return nil }
        released = true
        beatTask?.cancel()
        beatTask = nil
        guard state == .mine else { return nil }
        let ops = ops
        return { try? await ops.release() }
    }
}
