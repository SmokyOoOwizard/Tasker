import Foundation

struct DaemonInfo: Codable, Equatable, Sendable {
    var pid: Int
    var port: Int
    var token: String
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
    var version: String?
    var build: String?
    var workspaces: [DaemonWorkspace]

    var isSettled: Bool {
        !workspaces.contains { $0.state == "opening" }
    }
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
