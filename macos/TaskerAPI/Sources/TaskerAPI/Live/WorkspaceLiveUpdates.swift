import Foundation

struct WorkspaceLiveUpdates: Sendable {
    let client: TaskerAPIClient
    let clock: @Sendable () -> Date

    private var cache: NormalizedCache { client.cache }

    static let taskTypenames = ["TaskListItem", "TaskDetails", "TaskItem"]
    static let taskListFields: Set<String> = ["tasks", "taskTree", "columnTasks", "resolveTasks", "stats"]
    static let rootProjectFields: Set<String> = ["projects"]
    static let rootUserFields: Set<String> = ["users", "user", "agents", "agent", "agentTokens", "me"]
    static let projectFields: [String: Set<String>] = [
        "status": ["statuses", "status", "stats"],
        "statusSet": ["statusSets", "statusSet", "stats"],
        "taskType": ["taskTypes", "taskType", "stats"],
        "field": ["fields", "field"],
        "enum": ["enums", "enum"],
        "linkType": ["linkTypes", "linkType", "taskLinks", "stats"],
        "board": ["boards", "board", "columnTasks", "stats"],
        "series": ["seriesList", "series", "seriesHealth", "stats"],
        "comment": ["taskComments"]
    ]

    static let snapshotSelections: [GraphQLField] = [
        GraphQLField("id"),
        GraphQLField("projectId"),
        GraphQLField("title"),
        GraphQLField("statusId"),
        GraphQLField("seriesNumbers", selections: [GraphQLField("seriesId"), GraphQLField("number")]),
        GraphQLField("version")
    ]

    static let projectSnapshotSelections: [GraphQLField] = [GraphQLField("name")]

    static var liveTaskSelections: [GraphQLField] {
        LiveTaskQuery.selections.first?.selections.first { $0.name == "task" }?.selections ?? []
    }

    func events(reconnect: ReconnectPolicy) -> AsyncThrowingStream<LiveEvent, any Error> {
        let source = client.graphQL.subscribe(WorkspaceChangesSubscription(), reconnect: reconnect)
        let (stream, continuation) = AsyncThrowingStream<LiveEvent, any Error>.makeStream()
        let task = Task {
            var connections = 0
            do {
                for try await event in source {
                    switch event {
                    case .connected:
                        connections += 1
                        if connections > 1 { await cache.evictAll() }
                        continuation.yield(.connected(reconnected: connections > 1, at: clock()))
                    case .disconnected:
                        continuation.yield(.disconnected)
                    case .value(let data):
                        if let update = await apply(data.workspaceChanges) {
                            continuation.yield(.update(update))
                        }
                    }
                }
                continuation.finish()
            } catch {
                continuation.finish(throwing: error)
            }
        }
        continuation.onTermination = { _ in task.cancel() }
        return stream
    }

    func apply(_ change: WorkspaceChangesSubscription.Data.WorkspaceChanges) async -> LiveUpdate? {
        let author = change.author.map { ChangeAuthor(key: $0.key, name: $0.name) }
        let result: LiveChange?
        switch (change.entity, change.projectId, change.id) {
        case ("lock", _, _):
            result = nil
        case ("task", let projectId?, let id?):
            result = await applyTask(projectId: projectId, id: id)
        case ("task", let projectId?, nil):
            await cache.invalidate(projectId: projectId, fields: Self.taskListFields)
            result = .other(entity: change.entity)
        case ("project", _, let id?):
            result = await applyProject(id: id)
        case ("user", _, let id):
            if let id { await cache.evict(id: id) }
            await cache.invalidateRoot(fields: Self.rootUserFields)
            result = .other(entity: change.entity)
        case (let entity, let projectId?, let id) where Self.projectFields[entity] != nil:
            if let id { await cache.evict(id: id) }
            await cache.invalidate(projectId: projectId, fields: Self.projectFields[entity] ?? [])
            result = .other(entity: entity)
        default:
            await cache.evictAll()
            result = .reloaded
        }
        return result.map { LiveUpdate(change: $0, author: author, receivedAt: clock()) }
    }

    private func applyTask(projectId: UUID, id: UUID) async -> LiveChange {
        let cached = await cachedTask(id: id)
        await cache.invalidate(projectId: projectId, fields: Self.taskListFields)
        let fetched: LiveTaskQuery.Data
        do {
            fetched = try await client.graphQL.fetch(LiveTaskQuery(projectId: projectId, id: id))
        } catch {
            await cache.evict(id: id)
            return .other(entity: "task")
        }
        guard let task = fetched.project?.task else {
            await cache.evict(id: id)
            let vocabulary = await vocabulary(projectId: projectId)
            return .taskDeleted(cached.map { Self.named($0, vocabulary) }, id: id)
        }
        if let json = await cache.record(typename: "TaskDetails", id: id, selections: Self.liveTaskSelections) {
            for typename in Self.taskTypenames where typename != "TaskDetails" {
                await cache.patch(typename: typename, id: id, with: json, selections: Self.liveTaskSelections)
            }
        }
        let vocabulary = await vocabulary(projectId: projectId)
        let after = TaskSnapshot(
            id: task.id,
            projectId: task.projectId,
            title: task.title,
            statusId: task.statusId,
            statusName: vocabulary.statuses[task.statusId],
            reference: Self.reference(task.seriesNumbers.map { ($0.seriesId, $0.number) }, prefixes: vocabulary.prefixes),
            version: task.version
        )
        if cached == nil, task.createdAt == task.updatedAt {
            return .taskCreated(after)
        }
        return .taskUpdated(before: cached.map { Self.named($0, vocabulary) }, after: after)
    }

    private func applyProject(id: UUID) async -> LiveChange {
        let before = await cache.record(typename: "Project", id: id, selections: Self.projectSnapshotSelections)?["name"]?.stringValue
        let fetched: LiveProjectQuery.Data
        do {
            fetched = try await client.graphQL.fetch(LiveProjectQuery(id: id))
        } catch {
            await cache.evict(id: id)
            await cache.invalidateRoot(fields: Self.rootProjectFields)
            return .other(entity: "project")
        }
        guard let project = fetched.project else {
            await cache.evict(id: id)
            await cache.invalidateRoot(fields: Self.rootProjectFields)
            return .projectDeleted(name: before)
        }
        guard let before else {
            await cache.invalidateRoot(fields: Self.rootProjectFields)
            return .projectCreated(name: project.name)
        }
        return .projectRenamed(from: before, to: project.name)
    }

    private struct CachedTask: Decodable {
        struct Number: Decodable {
            var seriesId: UUID
            var number: Int
        }

        var id: UUID
        var projectId: UUID
        var title: String
        var statusId: UUID
        var seriesNumbers: [Number]
        var version: String
    }

    private struct Vocabulary {
        var statuses: [UUID: String] = [:]
        var prefixes: [UUID: String] = [:]
    }

    private func cachedTask(id: UUID) async -> (task: TaskSnapshot, numbers: [(UUID, Int)])? {
        for typename in Self.taskTypenames {
            guard let json = await cache.record(typename: typename, id: id, selections: Self.snapshotSelections),
                  let task = try? TaskerJSON.decode(CachedTask.self, from: json) else { continue }
            let snapshot = TaskSnapshot(id: task.id, projectId: task.projectId, title: task.title, statusId: task.statusId, version: task.version)
            return (snapshot, task.seriesNumbers.map { ($0.seriesId, $0.number) })
        }
        return nil
    }

    private func vocabulary(projectId: UUID) async -> Vocabulary {
        guard let project = try? await client.graphQL.fetch(ProjectVocabularyQuery(projectId: projectId), cachePolicy: .cacheFirst).project else {
            return Vocabulary()
        }
        return Vocabulary(
            statuses: Dictionary(project.statuses.data.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first }),
            prefixes: Dictionary(project.seriesList.data.map { ($0.id, $0.prefix) }, uniquingKeysWith: { first, _ in first })
        )
    }

    private static func named(_ cached: (task: TaskSnapshot, numbers: [(UUID, Int)]), _ vocabulary: Vocabulary) -> TaskSnapshot {
        var task = cached.task
        task.statusName = vocabulary.statuses[task.statusId]
        task.reference = reference(cached.numbers, prefixes: vocabulary.prefixes)
        return task
    }

    static func reference(_ numbers: [(UUID, Int)], prefixes: [UUID: String]) -> String? {
        for (seriesId, number) in numbers {
            if let prefix = prefixes[seriesId] { return "\(prefix)-\(number)" }
        }
        return nil
    }
}
