import Foundation
import Testing
@testable import TaskerAPI

@Suite struct NormalizedCacheTests {
    let taskerId = UUID(uuidString: "6cb8f29b-d6d5-4987-a61d-745df39b1ded")!

    @Test func storesEntitiesOnce() async throws {
        let cache = NormalizedCache()
        let json = try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json"))
        let changed = await cache.write(json, selections: ProjectsQuery.selections, variables: ProjectsQuery().variables)
        #expect(changed.contains("Project:6cb8f29b-d6d5-4987-a61d-745df39b1ded"))
        #expect(changed.contains(NormalizedCache.rootKey))
        #expect(await cache.recordCount == 4)

        let read = try #require(await cache.read(ProjectsQuery()))
        #expect(read == (try TaskerJSON.decode(ProjectsQuery.Data.self, from: json)))
    }

    @Test func entityUpdateIsSeenByOtherQueries() async throws {
        let cache = NormalizedCache()
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        let query = ProjectQuery(id: taskerId)
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("project-renamed.json")), selections: ProjectQuery.selections, variables: query.variables)

        let list = try #require(await cache.read(ProjectsQuery()))
        #expect(list.projects.data[0].name == "Tasker 2")
        #expect(list.projects.data[0].version == "b1")
        #expect(await cache.read(query)?.project?.stats.tasks == 185)
    }

    @Test func missesOnDifferentArguments() async throws {
        let cache = NormalizedCache()
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        #expect(await cache.read(ProjectsQuery(offset: 50)) == nil)
        #expect(await cache.read(ProjectQuery(id: taskerId)) == nil)
    }

    @Test func evictsById() async throws {
        let cache = NormalizedCache()
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        await cache.evict(id: taskerId)
        #expect(await cache.read(ProjectsQuery()) == nil)
        #expect(await cache.keys().contains("Project:0b9a1c52-27a4-4c41-9d2a-3b8e0c1f7a10"))
    }

    @Test func appliesWorkspaceChanges() async throws {
        let cache = NormalizedCache()
        let query = ProjectQuery(id: taskerId)
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("project-renamed.json")), selections: ProjectQuery.selections, variables: query.variables)

        await cache.apply(change: "task", projectId: taskerId, id: UUID())
        #expect(await cache.read(query) == nil)
        #expect(await cache.keys().contains(NormalizedCache.rootKey))

        await cache.apply(change: "lock", projectId: nil, id: UUID())
        #expect(await cache.keys().contains(NormalizedCache.rootKey))

        let created = UUID()
        await cache.apply(change: "project", projectId: created, id: created)
        #expect(await !cache.keys().contains(NormalizedCache.rootKey))
        #expect(await cache.keys().contains("Project:0b9a1c52-27a4-4c41-9d2a-3b8e0c1f7a10"))

        await cache.apply(change: "unknown", projectId: nil, id: nil)
        #expect(await cache.recordCount == 0)
    }

    @Test func notifiesChangedKeys() async throws {
        let cache = NormalizedCache()
        let changes = await cache.changes()
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        await cache.write(try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json")), selections: ProjectsQuery.selections, variables: [:])
        await cache.evict(id: taskerId)
        var iterator = changes.makeAsyncIterator()
        let first = await iterator.next()
        let second = await iterator.next()
        #expect(first?.count == 4)
        #expect(second == ["Project:6cb8f29b-d6d5-4987-a61d-745df39b1ded"])
    }
}
