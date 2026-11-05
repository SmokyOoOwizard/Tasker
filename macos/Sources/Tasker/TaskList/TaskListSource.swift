import Foundation
import TaskerAPI

struct TaskListRequest: Hashable, Sendable {
    var filter: TaskListFilter = .none
    var sort: [TaskSortKey] = []
}

struct TaskRowPage: Hashable, Sendable {
    var rows: [TaskRow]
    var totalCount: Int
    var levelCount: Int
}

protocol TaskListSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func flat(project: UUID, request: TaskListRequest, offset: Int, limit: Int) async throws -> TaskRowPage
    func tree(project: UUID, request: TaskListRequest, parent: UUID?, offset: Int, limit: Int) async throws -> TaskRowPage
    func count(project: UUID, request: TaskListRequest, status: UUID) async throws -> Int
    func setStatus(project: UUID, task: UUID, status: UUID, version: String) async throws
}

struct GraphQLTaskListSource: TaskListSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        guard let data = try await client.graphQL.fetch(TaskListVocabularyQuery(projectId: project)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        return TaskListVocabulary(data)
    }

    func flat(project: UUID, request: TaskListRequest, offset: Int, limit: Int) async throws -> TaskRowPage {
        let query = TaskListPageQuery(
            projectId: project, statusIds: request.filter.statusIds, typeIds: request.filter.typeIds, seriesIds: request.filter.seriesIds,
            field: request.filter.field, sort: TaskSort.string(request.sort), offset: offset, limit: limit)
        guard let page = try await client.graphQL.fetch(query).project?.tasks else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString)) }
        return TaskRowPage(rows: page.data.map(TaskRow.init), totalCount: page.totalCount, levelCount: page.totalCount)
    }

    func tree(project: UUID, request: TaskListRequest, parent: UUID?, offset: Int, limit: Int) async throws -> TaskRowPage {
        let query = TaskTreePageQuery(
            projectId: project, statusIds: request.filter.statusIds, typeIds: request.filter.typeIds, seriesIds: request.filter.seriesIds,
            field: request.filter.field, sort: TaskSort.string(request.sort), parentId: parent, levels: 1, offset: offset, limit: limit)
        guard let page = try await client.graphQL.fetch(query).project?.taskTree else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString)) }
        return TaskRowPage(rows: page.data.map(TaskRow.init), totalCount: page.totalCount, levelCount: page.topLevelCount)
    }

    func count(project: UUID, request: TaskListRequest, status: UUID) async throws -> Int {
        let statuses = request.filter.statusIds.map { $0.contains(status) ? [status] : [] } ?? [status]
        if statuses.isEmpty { return 0 }
        let query = TaskListCountQuery(
            projectId: project, statusIds: statuses, typeIds: request.filter.typeIds, seriesIds: request.filter.seriesIds, field: request.filter.field)
        return try await client.graphQL.fetch(query).project?.tasks.totalCount ?? 0
    }

    func setStatus(project: UUID, task: UUID, status: UUID, version: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateTask(
                path: .init(projectId: project.uuidString.lowercased(), id: task.uuidString.lowercased()),
                body: .json(.init(statusId: status.uuidString.lowercased(), version: version))
            ).ok.body.json
        }
    }
}
