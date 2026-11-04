import Foundation
import Testing
@testable import TaskerAPI

@Suite struct GraphQLClientTests {
    @Test func fetchesAndSendsOperation() async throws {
        let server = StubServer { _, _ in .fixture("projects.json") }
        let api = server.client()
        let data = try await api.graphQL.fetch(ProjectsQuery(limit: 50))
        #expect(data.projects.data.count == 2)

        let request = try #require(server.requests.first)
        #expect(request.method == "POST")
        #expect(request.url.path == "/w/\(server.id)/api/graphql")
        #expect(request.json?["operationName"] == .string("Projects"))
        #expect(request.json?["variables"] == .object(["limit": .int(50)]))
        #expect(request.json?["query"]?.stringValue == ProjectsQuery.document)
        #expect(request.authorization == nil)
    }

    @Test func cacheFirstSkipsNetwork() async throws {
        let server = StubServer { _, _ in .fixture("projects.json") }
        let api = server.client()
        _ = try await api.graphQL.fetch(ProjectsQuery())
        let cached = try await api.graphQL.fetch(ProjectsQuery(), cachePolicy: .cacheFirst)
        #expect(cached.projects.totalCount == 2)
        #expect(server.requests.count == 1)
        await #expect(throws: TaskerAPIError.self) {
            try await api.graphQL.fetch(ProjectsQuery(offset: 2), cachePolicy: .cacheOnly)
        }
    }

    @Test func throwsDomainErrors() async throws {
        let server = StubServer { _, _ in .fixture("project-not-found.json") }
        let api = server.client()
        await #expect(throws: TaskerAPIError.notFound("Project 6cb8f29b-d6d5-4987-a61d-745df39b1ded not found")) {
            try await api.graphQL.fetch(ProjectQuery(id: UUID()))
        }
    }

    @Test func watchFollowsCacheAndRefetchesAfterEviction() async throws {
        let server = StubServer { _, index in
            if index == 0 { return .fixture("projects.json") }
            return .json(Fixture.text("projects.json").replacingOccurrences(of: "\"Сайт\"", with: "\"Сайт 2\""))
        }
        let api = server.client()
        var updates = api.graphQL.watch(ProjectsQuery()).makeAsyncIterator()

        let first = try await updates.next()
        #expect(first?.projects.data.map(\.name) == ["Tasker", "Сайт"])

        let renamed = try GraphQLClient.parse(status: 200, body: Fixture.data("project-renamed.json"))
        await api.cache.write(renamed, selections: ProjectQuery.selections, variables: ProjectQuery(id: UUID(uuidString: "6cb8f29b-d6d5-4987-a61d-745df39b1ded")!).variables)
        let second = try await updates.next()
        #expect(second?.projects.data.map(\.name) == ["Tasker 2", "Сайт"])

        await api.cache.evict(id: UUID(uuidString: "0b9a1c52-27a4-4c41-9d2a-3b8e0c1f7a10")!)
        let third = try await updates.next()
        #expect(third?.projects.data.map(\.name) == ["Tasker", "Сайт 2"])
        #expect(server.requests.count == 2)
    }

    @Test func timesOut() async throws {
        let server = StubServer { _, _ in
            var response = StubResponse.fixture("projects.json")
            response.delay = .seconds(5)
            return response
        }
        let api = server.client(timeout: 0.3)
        await #expect(throws: TaskerAPIError.timedOut) {
            try await api.graphQL.fetch(ProjectsQuery())
        }
    }

    @Test func cancels() async throws {
        let server = StubServer { _, _ in
            var response = StubResponse.fixture("projects.json")
            response.delay = .seconds(5)
            return response
        }
        let api = server.client()
        let task = Task { try await api.graphQL.fetch(ProjectsQuery()) }
        try await Task.sleep(for: .milliseconds(100))
        task.cancel()
        await #expect(throws: CancellationError.self) {
            try await task.value
        }
    }
}

@Suite struct SubscriptionTests {
    @Test func streamsEventsAndCompletes() async throws {
        let server = StubServer { _, _ in .fixture("workspace-changes.sse") }
        let api = server.client()
        var events: [SubscriptionEvent<WorkspaceChangesSubscription.Data>] = []
        for try await event in api.graphQL.subscribe(WorkspaceChangesSubscription()) {
            events.append(event)
        }
        #expect(events.count == 3)
        #expect(events[0] == .connected)
        guard case .value(let first) = events[1], case .value(let second) = events[2] else {
            Issue.record("expected two values")
            return
        }
        #expect(first.workspaceChanges.entity == "task")
        #expect(first.workspaceChanges.id == UUID(uuidString: "19509c26-992a-475d-8946-91e3cb71f1d2"))
        #expect(second.workspaceChanges.entity == "unknown")
        #expect(second.workspaceChanges.projectId == nil)
        #expect(server.requests.first?.headers["Accept"] == "text/event-stream")
    }

    @Test func reconnectsAfterStreamEnds() async throws {
        let server = StubServer { _, index in
            if index == 0 {
                return StubResponse(status: 200, contentType: "text/event-stream", chunks: [Data(": connected\n\n".utf8)])
            }
            if index == 1 {
                return .json("{\"error\":\"Service unavailable\"}", status: 503)
            }
            return .fixture("workspace-changes.sse")
        }
        let api = server.client()
        var connected = 0
        var values = 0
        for try await event in api.graphQL.subscribe(WorkspaceChangesSubscription(), reconnect: ReconnectPolicy(initialDelay: .milliseconds(10))) {
            switch event {
            case .connected: connected += 1
            case .value: values += 1
            case .disconnected: break
            }
        }
        #expect(connected == 2)
        #expect(values == 2)
        #expect(server.requests.count == 3)
    }

    @Test func stopsOnDomainError() async throws {
        let server = StubServer { _, _ in .fixture("project-not-found.json", status: 404) }
        let api = server.client()
        await #expect(throws: TaskerAPIError.self) {
            for try await _ in api.graphQL.subscribe(WorkspaceChangesSubscription()) {}
        }
        #expect(server.requests.count == 1)
    }

    @Test func givesUpAfterMaxAttempts() async throws {
        let server = StubServer { _, _ in .json("{}", status: 502) }
        let api = server.client()
        await #expect(throws: TaskerAPIError.self) {
            for try await _ in api.graphQL.subscribe(WorkspaceChangesSubscription(), reconnect: ReconnectPolicy(initialDelay: .milliseconds(1), maxAttempts: 2)) {}
        }
        #expect(server.requests.count == 3)
    }

    @Test func liveUpdatesReloadEverythingOnUnknownChange() async throws {
        let server = StubServer { request, _ in
            if request.headers["Accept"] == "text/event-stream" { return .fixture("workspace-changes.sse") }
            return .fixture("projects.json")
        }
        let api = server.client()
        _ = try await api.graphQL.fetch(ProjectsQuery())
        #expect(await api.cache.recordCount == 4)
        var changes: [LiveChange] = []
        for try await event in api.liveUpdates(reconnect: .never) {
            if case .update(let update) = event { changes.append(update.change) }
        }
        #expect(changes == [.taskDeleted(nil, id: UUID(uuidString: "19509c26-992a-475d-8946-91e3cb71f1d2")!), .reloaded])
        #expect(await api.cache.recordCount == 0)
    }
}

@Suite struct RESTClientTests {
    @Test func createsProject() async throws {
        let server = StubServer { _, _ in .fixture("rest-project-created.json", status: 201) }
        let api = server.client()
        let project = try await api.rest.perform { client in
            try await client.createProject(body: .json(.init(name: "Сайт"))).created.body.json
        }
        #expect(project.name == "Сайт")
        #expect(project.createdAt == TaskerDate.parse("2026-11-04T05:00:00.123Z"))

        let request = try #require(server.requests.first)
        #expect(request.method == "POST")
        #expect(request.url.path == "/w/\(server.id)/api/projects")
        #expect(request.json == .object(["name": .string("Сайт")]))
    }

    @Test func mapsConflicts() async throws {
        let server = StubServer { _, index in
            index == 0 ? .fixture("rest-conflict-modified.json", status: 409) : .fixture("rest-conflict-locked.json", status: 409)
        }
        let api = server.client()
        let id = UUID().uuidString

        await #expect(throws: TaskerAPIError.conflict(Conflict(kind: .modified, message: "Project was changed by someone else"))) {
            try await api.rest.perform { client in
                try await client.updateProject(path: .init(projectId: id), body: .json(.init(name: "X", version: "1"))).ok.body.json
            }
        }

        let error = await #expect(throws: TaskerAPIError.self) {
            try await api.rest.perform { client in
                try await client.deleteProject(path: .init(projectId: id), query: .init(version: "1")).noContent
            }
        }
        guard case .conflict(let conflict) = error else {
            Issue.record("expected conflict")
            return
        }
        #expect(conflict.kind == .locked)
        #expect(conflict.heldBy?.name == "bob")
        #expect(conflict.heldBy?.expiresAt == TaskerDate.parse("2026-11-04T07:15:30Z"))
        #expect(server.requests.last?.url.query == "version=1")
    }

    @Test func mapsBadRequest() async throws {
        let server = StubServer { _, _ in .fixture("rest-bad-request.json", status: 400) }
        let api = server.client()
        await #expect(throws: TaskerAPIError.badRequest("Name is required")) {
            try await api.rest.perform { client in
                try await client.createProject(body: .json(.init(name: ""))).created.body.json
            }
        }
    }
}

@Suite struct AuthorizationTests {
    static func session(server: StubServer, access: String = "access-1", expires: Date? = nil) -> ServerSession {
        ServerSession(
            tokens: .init(accessToken: access, accessTokenExpiresAt: expires, refreshToken: "refresh-1", refreshTokenExpiresAt: nil),
            refresher: RESTClient.refresher(baseURL: server.endpoint().baseURL, session: server.client().rest.session))
    }

    @Test func refreshesAfterUnauthorizedForGraphQLAndREST() async throws {
        let server = StubServer { request, _ in
            if request.url.path.hasSuffix("/api/auth/refresh") { return .fixture("auth-session.json") }
            guard request.authorization == "Bearer access-2" else { return .fixture("graphql-unauthenticated.json", status: 401) }
            if request.url.path.hasSuffix("/api/graphql") { return .fixture("projects.json") }
            return .fixture("rest-project-created.json", status: 201)
        }
        let session = Self.session(server: server)
        let api = server.client(authorization: session)

        let data = try await api.graphQL.fetch(ProjectsQuery())
        #expect(data.projects.totalCount == 2)
        _ = try await api.rest.perform { try await $0.createProject(body: .json(.init(name: "Сайт"))).created.body.json }

        #expect(server.requests.map(\.authorization) == ["Bearer access-1", nil, "Bearer access-2", "Bearer access-2"])
        let refresh = try #require(server.requests.first { $0.url.path.hasSuffix("/api/auth/refresh") })
        #expect(refresh.json == .object(["refreshToken": .string("refresh-1")]))
        #expect(await session.current?.refreshToken == "refresh-2")
    }

    @Test func refreshesExpiredTokenOnceForConcurrentRequests() async throws {
        let server = StubServer { request, _ in
            if request.url.path.hasSuffix("/api/auth/refresh") {
                var response = StubResponse.fixture("auth-session.json")
                response.delay = .milliseconds(100)
                return response
            }
            return request.authorization == "Bearer access-2" ? .fixture("projects.json") : .fixture("graphql-unauthenticated.json", status: 401)
        }
        let session = Self.session(server: server, expires: Date().addingTimeInterval(-10))
        let api = server.client(authorization: session)
        async let first = api.graphQL.fetch(ProjectsQuery())
        async let second = api.graphQL.fetch(ProjectsQuery(limit: 1))
        _ = try await (first, second)
        #expect(server.requests.filter { $0.url.path.hasSuffix("/api/auth/refresh") }.count == 1)
    }

    @Test func signsOutWhenRefreshIsRejected() async throws {
        let server = StubServer { request, _ in
            if request.url.path.hasSuffix("/api/auth/refresh") { return .json("{\"error\":\"Session expired\"}", status: 401) }
            return .fixture("graphql-unauthenticated.json", status: 401)
        }
        let session = Self.session(server: server)
        let api = server.client(authorization: session)
        await #expect(throws: TaskerAPIError.unauthenticated("Sign in to read data")) {
            try await api.graphQL.fetch(ProjectsQuery())
        }
        #expect(await session.current == nil)
    }

    @Test func signsIn() async throws {
        let server = StubServer { _, _ in .fixture("auth-session.json") }
        let session = try await RESTClient.signIn(baseURL: server.endpoint().baseURL, login: "alice", password: "secret", session: server.client().rest.session)
        #expect(await session.current?.accessToken == "access-2")
        #expect(server.requests.first?.json == .object(["login": .string("alice"), "password": .string("secret")]))
    }

    @Test func localEndpointAddressesWorkspace() {
        let endpoint = TaskerEndpoint.local(port: 5719, workspace: "my folder")
        #expect(endpoint.baseURL.absoluteString == "http://127.0.0.1:5719/w/my%20folder")
        #expect(endpoint.graphQLURL.absoluteString == "http://127.0.0.1:5719/w/my%20folder/api/graphql")
    }
}
