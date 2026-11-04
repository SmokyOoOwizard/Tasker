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

    init?(status: DaemonStatus) {
        guard let workspace = status.workspaces.first(where: { $0.state == "open" && $0.key != nil }), let key = workspace.key else { return nil }
        self.init(port: status.port, key: key, name: URL(fileURLWithPath: workspace.path).lastPathComponent)
    }
}

struct LiveToast: Identifiable, Equatable, Sendable {
    let id: UUID
    let message: LiveToastMessage
}

@MainActor
@Observable
final class LiveWorkspace {
    private(set) var address: LiveWorkspaceAddress?
    private(set) var client: TaskerAPIClient?
    private(set) var connection: LiveConnection = .connecting
    private(set) var lastUpdate: Date?
    private(set) var toasts: [LiveToast] = []

    @ObservationIgnored private let makeClient: (LiveWorkspaceAddress) -> TaskerAPIClient
    @ObservationIgnored private let reconnect: ReconnectPolicy
    @ObservationIgnored private let retryDelay: Duration
    @ObservationIgnored private let toastDuration: Duration
    @ObservationIgnored private let ownAuthorKey: String
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var everConnected = false

    static let maxToasts = 3

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
        self.toastDuration = toastDuration
        self.ownAuthorKey = ownAuthorKey
    }

    func follow(_ state: DaemonConnectionState) {
        guard case .running(let status) = state else { return }
        guard let address = LiveWorkspaceAddress(status: status) else {
            stop()
            address = nil
            client = nil
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
        lastUpdate = nil
        task = Task { [weak self] in
            await self?.run(client)
        }
    }

    func stop() {
        task?.cancel()
        task = nil
    }

    func dismiss(_ id: UUID) {
        toasts.removeAll { $0.id == id }
    }

    func handle(_ event: LiveEvent, client: TaskerAPIClient) async {
        switch event {
        case .connected(let reconnected, let at):
            if everConnected, !reconnected { await client.cache.evictAll() }
            let restored = everConnected
            everConnected = true
            connection = .live
            lastUpdate = at
            if restored { show(.reconnected) }
        case .disconnected:
            connection = .offline
        case .update(let update):
            lastUpdate = update.receivedAt
            if let message = LiveToastMessage(update, ownAuthorKey: ownAuthorKey) { show(message) }
        }
    }

    func show(_ message: LiveToastMessage) {
        let toast = LiveToast(id: UUID(), message: message)
        toasts.append(toast)
        if toasts.count > Self.maxToasts { toasts.removeFirst(toasts.count - Self.maxToasts) }
        let duration = toastDuration
        Task { [weak self] in
            try? await Task.sleep(for: duration)
            self?.dismiss(toast.id)
        }
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
