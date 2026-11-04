import Foundation
import Testing
@testable import TaskerAPI

@Suite(.enabled(if: LiveDaemon.endpoint != nil, "TASKER_LIVE_PORT and TASKER_LIVE_WORKSPACE point to a running daemon"))
struct LiveDaemonTests {
    @Test(.timeLimit(.minutes(1))) func readsCreatesAndReceivesEvents() async throws {
        let api = TaskerAPIClient(endpoint: try #require(LiveDaemon.endpoint))

        let before = try await api.graphQL.fetch(ProjectsQuery())
        #expect(before.projects.totalCount == before.projects.data.count)

        var changes = api.workspaceChanges(updatingCache: false).makeAsyncIterator()
        #expect(try await changes.next() == .connected)

        var watched = api.graphQL.watch(ProjectsQuery()).makeAsyncIterator()
        #expect(try await watched.next()?.projects.totalCount == before.projects.totalCount)

        let name = "Live " + UUID().uuidString.prefix(8)
        let created = try await api.rest.perform { client in
            try await client.createProject(body: .json(.init(name: name))).created.body.json
        }
        #expect(created.name == name)

        var event: WorkspaceChangesSubscription.Data.WorkspaceChanges?
        while event == nil, let next = try await changes.next() {
            if case .value(let change) = next { event = change }
        }
        let change = try #require(event)
        #expect(["project", "unknown"].contains(change.entity))

        await api.cache.apply(change: change.entity, projectId: change.projectId, id: change.id)
        var after: ProjectsQuery.Data?
        while after == nil, let value = try await watched.next() {
            if value.projects.data.contains(where: { $0.name == name }) { after = value }
        }
        #expect(after?.projects.totalCount == before.projects.totalCount + 1)

        let projectId = try #require(UUID(uuidString: created.id))
        let fetched = try await api.graphQL.fetch(ProjectQuery(id: projectId))
        #expect(fetched.project?.name == name)
        #expect(fetched.project?.stats.tasks == 0)

        await #expect(throws: TaskerAPIError.self) {
            try await api.rest.perform { client in
                try await client.updateProject(path: .init(projectId: created.id), body: .json(.init(name: name + "!", version: "stale"))).ok.body.json
            }
        }

        _ = try await api.rest.perform { client in
            try await client.deleteProject(path: .init(projectId: created.id), query: .init(version: created.version)).noContent
        }
        let missing = await #expect(throws: TaskerAPIError.self) {
            try await api.rest.perform { client in
                try await client.deleteProject(path: .init(projectId: created.id), query: .init(version: created.version)).noContent
            }
        }
        guard case .notFound = missing else {
            Issue.record("expected notFound, got \(String(describing: missing))")
            return
        }
    }
}

enum LiveDaemon {
    static var endpoint: TaskerEndpoint? {
        let environment = ProcessInfo.processInfo.environment
        guard let port = environment["TASKER_LIVE_PORT"].flatMap(Int.init), let workspace = environment["TASKER_LIVE_WORKSPACE"] else { return nil }
        return .local(port: port, workspace: workspace)
    }
}
