import Foundation

struct DaemonInfo: Codable, Equatable, Sendable {
    var pid: Int
    var port: Int
    var token: String
}

struct DaemonWorker: Decodable, Equatable, Sendable {
    var pid: Int
    var version: String
    var build: String
    var role: String

    var isActive: Bool { role == "active" }
}

struct DaemonWorkspace: Decodable, Equatable, Sendable {
    var path: String
    var state: String
    var key: String? = nil
    var error: String?
}

struct DaemonStatus: Decodable, Equatable, Sendable {
    var pid: Int
    var port: Int
    var workers: [DaemonWorker]
    var workspaces: [DaemonWorkspace]

    var activeVersion: String? {
        workers.first(where: \.isActive)?.version
    }

    var isSettled: Bool {
        !workspaces.contains { $0.state == "opening" }
    }

    var isSupervised: Bool { !workers.isEmpty }
}

struct DaemonUpgradeResult: Decodable, Equatable, Sendable {
    var ok: Bool
    var message: String
}

enum DaemonFailure: Equatable, Sendable {
    case helperMissing
    case notResponding
    case startFailed(String)
    case updateFailed(String)
}

enum DaemonConnectionState: Equatable, Sendable {
    case starting
    case running(DaemonStatus)
    case versionMismatch(daemon: String?, app: String, status: DaemonStatus)
    case stopped(DaemonFailure)

    var status: DaemonStatus? {
        switch self {
        case .running(let status), .versionMismatch(_, _, let status): status
        case .starting, .stopped: nil
        }
    }
}
