import Foundation
import TaskerAPI

struct AdminAgent: Identifiable, Hashable, Sendable {
    var id: UUID
    var name: String
    var version: String

    static let localName = "agent"

    var isLocalDefault: Bool { name == Self.localName }
}

struct AdminMcp: Equatable, Sendable {
    var daemon: DaemonState
    var port: Int?
    var url: String?
    var workspace: String?
    var workspaceState: WorkspaceState?
    var error: String?
    var agentHeader: String

    var isReady: Bool { daemon == .running && workspaceState == .open && url != nil }

    func command(agent: UUID?) -> String? {
        guard let url else { return nil }
        var text = "claude mcp add --transport http tasker \(url)"
        if let agent { text += " --header \"\(agentHeader): \(agent.uuidString.lowercased())\"" }
        return text
    }
}

struct AdminNumberConflict: Hashable, Sendable {
    var project: UUID
    var projectName: String
    var series: UUID
    var prefix: String
    var number: Int
    var taskCount: Int
}

struct AdminLinkCycle: Hashable, Sendable {
    var projectName: String
    var typeName: String
    var path: String
}

enum AdminProblemKind: Hashable, Sendable {
    case unreadableFile(path: String, error: String)
    case secondFolder(path: String)
    case numberConflict(AdminNumberConflict)
    case prefixConflict(project: String, prefix: String)
    case invalidSeries(project: String, count: Int)
    case unreadableSeries(project: String, count: Int)
    case invalidLinks(project: UUID, name: String, count: Int)
    case unreadableLinks(project: String, count: Int)
    case linkCycle(AdminLinkCycle)
}

struct AdminProblem: Identifiable, Hashable, Sendable {
    var kind: AdminProblemKind
    var id: String

    init(_ kind: AdminProblemKind) {
        self.kind = kind
        switch kind {
        case .unreadableFile(let path, _): id = "file|\(path)"
        case .secondFolder(let path): id = "folder|\(path)"
        case .numberConflict(let value): id = "number|\(value.series)|\(value.number)"
        case .prefixConflict(let project, let prefix): id = "prefix|\(project)|\(prefix)"
        case .invalidSeries(let project, _): id = "invalidSeries|\(project)"
        case .unreadableSeries(let project, _): id = "unreadableSeries|\(project)"
        case .invalidLinks(let project, _, _): id = "invalidLinks|\(project)"
        case .unreadableLinks(let project, _): id = "unreadableLinks|\(project)"
        case .linkCycle(let value): id = "cycle|\(value.projectName)|\(value.path)"
        }
    }
}

struct AdminProblems: Equatable, Sendable {
    var items: [AdminProblem] = []

    var count: Int { items.count }

    var projectsWithConflicts: [(project: UUID, series: UUID, prefix: String)] {
        var seen = Set<UUID>()
        return items.compactMap { item in
            guard case .numberConflict(let value) = item.kind, seen.insert(value.series).inserted else { return nil }
            return (value.project, value.series, value.prefix)
        }
    }

    var projectsWithInvalidLinks: [(project: UUID, name: String)] {
        var seen = Set<UUID>()
        return items.compactMap { item in
            guard case .invalidLinks(let project, let name, _) = item.kind, seen.insert(project).inserted else { return nil }
            return (project, name)
        }
    }
}
