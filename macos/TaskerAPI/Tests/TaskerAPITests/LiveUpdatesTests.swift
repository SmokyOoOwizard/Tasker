import Foundation
import os
import Testing
@testable import TaskerAPI

@Suite struct LiveUpdatesTests {
    static let projectId = UUID(uuidString: "6cb8f29b-d6d5-4987-a61d-745df39b1ded")!
    static let moved = UUID(uuidString: "19509c26-992a-475d-8946-91e3cb71f1d2")!
    static let removed = UUID(uuidString: "2a7f0c1e-5b3d-4e8a-9c6f-1d2e3f4a5b6c")!
    static let created = UUID(uuidString: "3b8e1d2f-6c4e-4f9b-8d7a-2e3f4a5b6c7d")!
    static let todo = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
    static let doing = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
    static let now = Date(timeIntervalSince1970: 1_800_000_000)

    static func workspace(stream: @escaping @Sendable (Int) -> StubResponse) -> StubServer {
        let streams = Counter()
        let lists = Counter()
        return StubServer { request, _ in
            if request.headers["Accept"] == "text/event-stream" { return stream(streams.next()) }
            switch request.json?["operationName"]?.stringValue {
            case "Projects":
                return .fixture("projects.json")
            case "ProjectTasks":
                return .fixture(lists.next() == 0 ? "live-tasks.json" : "live-tasks-after.json")
            case "ProjectVocabulary":
                return .fixture("project-vocabulary.json")
            case "LiveTask":
                switch request.json?["variables"]?["id"]?.stringValue {
                case moved.uuidString.lowercased(): return .fixture("live-task-moved.json")
                case created.uuidString.lowercased(): return .fixture("live-task-created.json")
                default: return .fixture("live-task-missing.json")
                }
            default:
                return StubResponse(status: 500)
            }
        }
    }

    static func operations(_ server: StubServer, _ name: String) -> Int {
        server.requests.filter { $0.json?["operationName"]?.stringValue == name }.count
    }

    static func eventually(_ condition: () -> Bool) async {
        for _ in 0..<200 where !condition() {
            try? await Task.sleep(for: .milliseconds(10))
        }
    }

    @Test func describesTaskChangesWithAuthorStatusAndReference() async throws {
        let server = Self.workspace { _ in .fixture("live-task-changes.sse") }
        let api = server.client()
        let list = ProjectTasksQuery(projectId: Self.projectId, sort: "-updated", limit: 10)
        _ = try await api.graphQL.fetch(list)

        var updates: [LiveUpdate] = []
        for try await event in api.liveUpdates(reconnect: .never, clock: { Self.now }) {
            if case .update(let update) = event { updates.append(update) }
        }

        #expect(updates.count == 3)
        let before = TaskSnapshot(id: Self.moved, projectId: Self.projectId, title: "Вход по паролю", statusId: Self.todo, statusName: "Сделать", reference: "TSK-131", version: "v1")
        let after = TaskSnapshot(id: Self.moved, projectId: Self.projectId, title: "Вход по паролю", statusId: Self.doing, statusName: "В работе", reference: "TSK-131", version: "v2")
        #expect(updates[0] == LiveUpdate(change: .taskUpdated(before: before, after: after), author: ChangeAuthor(key: "user:5c0ffee0-0000-4000-8000-000000000001", name: "Claude"), receivedAt: Self.now))
        #expect(updates[1].change == .taskCreated(TaskSnapshot(id: Self.created, projectId: Self.projectId, title: "Тёмная тема", statusId: Self.todo, statusName: "Сделать", reference: "TSK-133", version: "u1")))
        #expect(updates[1].author == nil)
        let gone = TaskSnapshot(id: Self.removed, projectId: Self.projectId, title: "Экспорт в CSV", statusId: Self.todo, statusName: "Сделать", reference: "TSK-132", version: "w1")
        #expect(updates[2].change == .taskDeleted(gone, id: Self.removed))
        #expect(updates[2].author?.key == ChangeAuthor.localKey)
        #expect(Self.operations(server, "ProjectVocabulary") == 1)
    }

    @Test func updatesOnlyTheAffectedEntityAndLists() async throws {
        let server = Self.workspace { _ in .fixture("live-task-changes.sse") }
        let api = server.client()
        let list = ProjectTasksQuery(projectId: Self.projectId, sort: "-updated", limit: 10)
        _ = try await api.graphQL.fetch(list)
        _ = try await api.graphQL.fetch(ProjectsQuery())

        for try await _ in api.liveUpdates(reconnect: .never) {}

        let item = await api.cache.record(typename: "TaskListItem", id: Self.moved, selections: [GraphQLField("statusId"), GraphQLField("version")])
        #expect(item?["statusId"] == .string(Self.doing.uuidString.lowercased()))
        #expect(item?["version"] == .string("v2"))
        #expect(await api.cache.contains(typename: "TaskListItem", id: Self.removed) == false)
        #expect(await api.cache.read(list) == nil)
        #expect(await api.cache.read(ProjectsQuery()) != nil)
        #expect(Self.operations(server, "Projects") == 1)

        let refreshed = try await api.graphQL.fetch(list, cachePolicy: .cacheFirst)
        #expect(refreshed.project?.tasks.data.map(\.title) == ["Тёмная тема", "Вход по паролю", "Экспорт в CSV"])
    }

    @Test func watchersRefetchOnlyTheirOwnAffectedQuery() async throws {
        let server = Self.workspace { _ in .fixture("live-task-changes.sse") }
        let api = server.client()
        let list = ProjectTasksQuery(projectId: Self.projectId, sort: "-updated", limit: 10)
        let projects = Task {
            var values = 0
            for try await _ in api.graphQL.watch(ProjectsQuery()) { values += 1 }
            return values
        }
        let tasks = Task {
            var titles: [[String]] = []
            for try await value in api.graphQL.watch(list) {
                titles.append(value.project?.tasks.data.map(\.title) ?? [])
                if titles.last?.count == 3 { break }
            }
            return titles
        }
        await Self.eventually { Self.operations(server, "Projects") == 1 && Self.operations(server, "ProjectTasks") == 1 }

        for try await _ in api.liveUpdates(reconnect: .never) {}

        let titles = try await tasks.value
        #expect(titles.first == ["Вход по паролю", "Экспорт в CSV"])
        #expect(titles.last == ["Тёмная тема", "Вход по паролю", "Экспорт в CSV"])
        projects.cancel()
        #expect(Self.operations(server, "Projects") == 1)
    }

    @Test func reconnectsAndReloadsWhatIsLoaded() async throws {
        let server = Self.workspace { index in
            if index == 0 { return StubResponse(status: 200, contentType: "text/event-stream", chunks: [Data(": connected\n\n".utf8)]) }
            if index == 1 { return .json("{\"error\":\"Service unavailable\"}", status: 503) }
            return StubResponse(status: 200, contentType: "text/event-stream", chunks: [Data(": connected\n\nevent: complete\ndata:\n\n".utf8)])
        }
        let api = server.client()
        let emitted = Counter()
        let watcher = Task {
            for try await _ in api.graphQL.watch(ProjectsQuery()) { _ = emitted.next() }
        }
        await Self.eventually { emitted.value == 1 }

        var events: [LiveEvent] = []
        for try await event in api.liveUpdates(reconnect: ReconnectPolicy(initialDelay: .milliseconds(10)), clock: { Self.now }) {
            events.append(event)
        }

        #expect(events == [.connected(reconnected: false, at: Self.now), .disconnected, .disconnected, .connected(reconnected: true, at: Self.now)])
        await Self.eventually { Self.operations(server, "Projects") == 2 }
        try? await Task.sleep(for: .milliseconds(100))
        watcher.cancel()
        #expect(Self.operations(server, "Projects") == 2)
        #expect(emitted.value == 1)
    }

    @Test func describesProjectChanges() async throws {
        let renamed = Fixture.text("project-renamed.json")
        let server = StubServer { request, _ in
            if request.headers["Accept"] == "text/event-stream" {
                let id = "6cb8f29b-d6d5-4987-a61d-745df39b1ded"
                let body = ": connected\n\nevent: next\ndata: {\"data\":{\"workspaceChanges\":{\"__typename\":\"WorkspaceChange\",\"entity\":\"project\",\"projectId\":\"\(id)\",\"id\":\"\(id)\",\"author\":null}}}\n\nevent: complete\ndata:\n\n"
                return StubResponse(status: 200, contentType: "text/event-stream", chunks: [Data(body.utf8)])
            }
            if request.json?["operationName"]?.stringValue == "LiveProject" { return .json(renamed) }
            return .fixture("projects.json")
        }
        let api = server.client()
        _ = try await api.graphQL.fetch(ProjectsQuery())
        var changes: [LiveChange] = []
        for try await event in api.liveUpdates(reconnect: .never) {
            if case .update(let update) = event { changes.append(update.change) }
        }
        #expect(changes == [.projectRenamed(from: "Tasker", to: "Tasker 2")])
        #expect(await api.cache.read(ProjectsQuery())?.projects.data.map(\.name) == ["Tasker 2", "Сайт"])
        #expect(Self.operations(server, "Projects") == 1)
    }
}

final class Counter: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: 0)

    func next() -> Int { state.withLock { value in defer { value += 1 }; return value } }

    var value: Int { state.withLock { $0 } }
}
