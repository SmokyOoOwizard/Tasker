import Foundation
import TaskerAPI

struct BoardColumnInfo: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var statusIds: [UUID]
    var conditions: [BoardCondition]
}

struct BoardInfo: Hashable, Sendable {
    var id: UUID
    var name: String
    var setIds: [UUID]
    var columns: [BoardColumnInfo]
}

struct BoardRowPage: Hashable, Sendable {
    var rows: [TaskRow]
    var total: Int
}

struct BoardMoved: Hashable, Sendable {
    var statusId: UUID
    var version: String
}

protocol BoardViewSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func board(project: UUID, id: UUID) async throws -> BoardInfo?
    func columnPage(project: UUID, board: UUID, column: UUID, field: [String], offset: Int, limit: Int) async throws -> BoardRowPage
    func taskPage(project: UUID, typeIds: [UUID], offset: Int, limit: Int) async throws -> BoardRowPage
    func move(project: UUID, board: UUID, column: UUID, task: UUID, version: String) async throws -> BoardMoved
}

struct GraphQLBoardViewSource: BoardViewSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project)
    }

    func board(project: UUID, id: UUID) async throws -> BoardInfo? {
        guard let record = try await GraphQLBoardEditorSource(client: client).board(project: project, id: id) else { return nil }
        let columns = record.draft.columns.compactMap { column in
            column.serverId.map { BoardColumnInfo(id: $0, name: column.name, statusIds: column.statusIds, conditions: column.conditions) }
        }
        return BoardInfo(id: record.id, name: record.draft.name, setIds: record.draft.setIds, columns: columns)
    }

    func columnPage(project: UUID, board: UUID, column: UUID, field: [String], offset: Int, limit: Int) async throws -> BoardRowPage {
        let query = BoardColumnTasksQuery(projectId: project, boardId: board, columnId: column, offset: offset, limit: limit)
        guard let page = try await client.graphQL.fetch(query).project?.columnTasks else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: column.uuidString))
        }
        return BoardRowPage(rows: page.data.map(TaskRow.init), total: page.totalCount)
    }

    func taskPage(project: UUID, typeIds: [UUID], offset: Int, limit: Int) async throws -> BoardRowPage {
        let query = TaskListPageQuery(projectId: project, statusIds: nil, typeIds: typeIds, seriesIds: nil, field: nil, sort: nil, offset: offset, limit: limit)
        guard let page = try await client.graphQL.fetch(query).project?.tasks else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        return BoardRowPage(rows: page.data.map(TaskRow.init), total: page.totalCount)
    }

    func move(project: UUID, board: UUID, column: UUID, task: UUID, version: String) async throws -> BoardMoved {
        let item = try await client.rest.perform {
            try await $0.moveTaskToBoardColumn(
                path: .init(projectId: project.uuidString.lowercased(), id: board.uuidString.lowercased(), columnId: column.uuidString.lowercased()),
                body: .json(.init(taskId: task.uuidString.lowercased(), version: version))
            ).ok.body.json
        }
        return BoardMoved(statusId: UUID(uuidString: item.statusId) ?? UUID(), version: item.version)
    }
}
