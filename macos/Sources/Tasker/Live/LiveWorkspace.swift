import Foundation
import Observation
import TaskerAPI

enum LiveConnection: Equatable, Sendable {
    case connecting
    case live
    case offline
}

struct LiveWorkspaceAddress: Hashable, Sendable {
    var port: Int
    var key: String
    var name: String

    init(port: Int, key: String, name: String) {
        self.port = port
        self.key = key
        self.name = name
    }

    init?(status: DaemonStatus, path: String? = nil) {
        guard let workspace = status.workspaces.first(where: { workspace in
            workspace.state == "open" && workspace.key != nil && (path.map { WorkspacePath.same($0, workspace.path) } ?? true)
        }), let key = workspace.key else { return nil }
        self.init(port: status.port, key: key, name: WorkspacePath.name(workspace.path))
    }
}

struct LiveToast: Identifiable, Equatable, Sendable {
    let id: UUID
    let message: LiveToastMessage
}

@MainActor
@Observable
final class ToastQueue {
    static let maxToasts = 3

    private(set) var toasts: [LiveToast] = []

    @ObservationIgnored private let duration: Duration

    init(duration: Duration = .milliseconds(2600)) {
        self.duration = duration
    }

    func show(_ message: LiveToastMessage) {
        let toast = LiveToast(id: UUID(), message: message)
        toasts.append(toast)
        if toasts.count > Self.maxToasts { toasts.removeFirst(toasts.count - Self.maxToasts) }
        let duration = duration
        Task { [weak self] in
            try? await Task.sleep(for: duration)
            self?.dismiss(toast.id)
        }
    }

    func dismiss(_ id: UUID) {
        toasts.removeAll { $0.id == id }
    }
}

@MainActor
@Observable
final class LiveWorkspace {
    private(set) var address: LiveWorkspaceAddress?
    private(set) var client: TaskerAPIClient?
    private(set) var connection: LiveConnection = .connecting
    private(set) var projects: [ProjectFields]?
    private(set) var revision = 0

    @ObservationIgnored private let makeClient: (LiveWorkspaceAddress) -> TaskerAPIClient
    @ObservationIgnored private let reconnect: ReconnectPolicy
    @ObservationIgnored private let retryDelay: Duration
    let queue: ToastQueue
    @ObservationIgnored private let ownAuthorKey: String
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var projectsTask: Task<Void, Never>?
    @ObservationIgnored private var everConnected = false

    static let maxToasts = ToastQueue.maxToasts

    var toasts: [LiveToast] { queue.toasts }

    init(
        makeClient: @escaping (LiveWorkspaceAddress) -> TaskerAPIClient = { TaskerAPIClient(endpoint: .local(port: $0.port, workspace: $0.key)) },
        reconnect: ReconnectPolicy = ReconnectPolicy(initialDelay: .milliseconds(500), maxDelay: .seconds(5)),
        retryDelay: Duration = .seconds(2),
        toastDuration: Duration = .milliseconds(2600),
        ownAuthorKey: String = ChangeAuthor.localKey
    ) {
        self.makeClient = makeClient
        self.reconnect = reconnect
        self.retryDelay = retryDelay
        queue = ToastQueue(duration: toastDuration)
        self.ownAuthorKey = ownAuthorKey
    }

    func follow(_ state: DaemonConnectionState) {
        guard case .running(let status) = state else { return }
        follow(LiveWorkspaceAddress(status: status))
    }

    func follow(_ state: DaemonConnectionState, selected path: String?) {
        guard case .running(let status) = state else { return }
        follow(path.flatMap { LiveWorkspaceAddress(status: status, path: $0) })
    }

    private func follow(_ address: LiveWorkspaceAddress?) {
        guard let address else {
            stop()
            self.address = nil
            client = nil
            projects = nil
            return
        }
        guard address != self.address else { return }
        start(address)
    }

    func start(_ address: LiveWorkspaceAddress) {
        stop()
        let client = makeClient(address)
        self.address = address
        self.client = client
        connection = .connecting
        everConnected = false
        projects = nil
        task = Task { [weak self] in
            await self?.run(client)
        }
        projectsTask = Task { [weak self] in
            await LiveQuery.follow(client, ProjectsQuery(limit: 200)) { data in
                self?.projects = data.projects.data
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
        projectsTask?.cancel()
        projectsTask = nil
    }

    func dismiss(_ id: UUID) {
        queue.dismiss(id)
    }

    func handle(_ event: LiveEvent, client: TaskerAPIClient) async {
        switch event {
        case .connected(let reconnected, let at):
            if everConnected, !reconnected { await client.cache.evictAll() }
            let restored = everConnected
            everConnected = true
            connection = .live
            if restored {
                revision += 1
                show(.reconnected)
            }
        case .disconnected:
            connection = .offline
        case .update(let update):
            revision += 1
            if let message = LiveToastMessage(update, ownAuthorKey: ownAuthorKey) { show(message) }
        }
    }

    func show(_ message: LiveToastMessage) {
        queue.show(message)
    }

    private func run(_ client: TaskerAPIClient) async {
        while !Task.isCancelled {
            do {
                for try await event in client.liveUpdates(reconnect: reconnect) {
                    await handle(event, client: client)
                }
            } catch {}
            if Task.isCancelled { return }
            connection = .offline
            try? await Task.sleep(for: retryDelay)
        }
    }
}
