import Foundation
import TaskerAPI

protocol AdminSource: Sendable {
    func agents() async throws -> [AdminAgent]
    func mcp() async throws -> AdminMcp
    func problems() async throws -> AdminProblems
    func createAgent(name: String) async throws
    func renameAgent(_ agent: AdminAgent, name: String) async throws
    func deleteAgent(_ agent: AdminAgent) async throws
    func cleanup(project: UUID) async throws -> AdminCleanupResult
}

struct AdminCleanupResult: Equatable, Sendable {
    var changes: Int
    var skipped: Bool
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLAdminSource: AdminSource {
    static let secondFolderMarker = "Second folder of the project"

    let client: TaskerAPIClient

    func agents() async throws -> [AdminAgent] {
        try await client.graphQL.fetch(AdminAgentsQuery()).agents.data
            .map { AdminAgent(id: $0.id, name: $0.username, version: $0.version) }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    func mcp() async throws -> AdminMcp {
        let data = try await client.graphQL.fetch(AdminMcpQuery()).workspaceMcp
        return AdminMcp(
            daemon: data.daemon, port: data.port, url: data.url, workspace: data.workspace, workspaceState: data.workspaceState,
            error: data.error, agentHeader: data.agentHeader)
    }

    func problems() async throws -> AdminProblems {
        let data = try await client.graphQL.fetch(AdminProblemsQuery())
        var items: [AdminProblem] = data.workspaceProblems.data.map { problem in
            problem.error.hasPrefix(Self.secondFolderMarker)
                ? AdminProblem(.secondFolder(path: problem.path))
                : AdminProblem(.unreadableFile(path: problem.path, error: problem.error))
        }
        for project in data.projects.data {
            let prefixes = Dictionary(project.seriesList.data.map { ($0.id, $0.prefix) }, uniquingKeysWith: { first, _ in first })
            let series = project.seriesHealth
            for conflict in series.numberConflicts {
                items.append(AdminProblem(.numberConflict(AdminNumberConflict(
                    project: project.id, projectName: project.name, series: conflict.seriesId, prefix: prefixes[conflict.seriesId] ?? "",
                    number: conflict.number, taskCount: conflict.taskIds.count))))
            }
            for conflict in series.prefixConflicts {
                items.append(AdminProblem(.prefixConflict(project: project.name, prefix: conflict.prefix)))
            }
            if series.tasksWithInvalidSeries > 0 {
                items.append(AdminProblem(.invalidSeries(project: project.name, count: series.tasksWithInvalidSeries)))
            }
            if series.unreadableSeriesFiles > 0 {
                items.append(AdminProblem(.unreadableSeries(project: project.name, count: series.unreadableSeriesFiles)))
            }
            let links = project.linkHealth
            if links.tasksWithInvalidLinks > 0 {
                items.append(AdminProblem(.invalidLinks(project: project.id, name: project.name, count: links.tasksWithInvalidLinks)))
            }
            if links.unreadableFiles > 0 {
                items.append(AdminProblem(.unreadableLinks(project: project.name, count: links.unreadableFiles)))
            }
            for cycle in links.cycles {
                let path = cycle.path.map { task -> String in
                    if let number = task.seriesNumbers.first(where: { prefixes[$0.seriesId] != nil }), let prefix = prefixes[number.seriesId] {
                        return "\(prefix)-\(number.number)"
                    }
                    return task.title
                }
                items.append(AdminProblem(.linkCycle(AdminLinkCycle(
                    projectName: project.name, typeName: cycle.typeName, path: path.joined(separator: " → ")))))
            }
        }
        return AdminProblems(items: items)
    }

    func createAgent(name: String) async throws {
        _ = try await client.rest.perform { try await $0.createAgent(body: .json(.init(username: name))).created.body.json }
    }

    func renameAgent(_ agent: AdminAgent, name: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateAgent(path: .init(id: agent.id.wire), body: .json(.init(username: name, version: agent.version))).ok.body.json
        }
    }

    func deleteAgent(_ agent: AdminAgent) async throws {
        try await client.rest.perform {
            try await $0.deleteAgent(path: .init(id: agent.id.wire), query: .init(version: agent.version)).noContent
        }
    }

    func cleanup(project: UUID) async throws -> AdminCleanupResult {
        let report = try await client.rest.perform { try await $0.cleanupProject(path: .init(projectId: project.wire)).ok.body.json }
        return AdminCleanupResult(changes: report.changes.count, skipped: report.skipped || report.linksSkipReason != nil)
    }
}
