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

struct BoardParentRef: Hashable, Sendable {
    var id: UUID
    var title: String
    var seriesNumbers: [TaskSeriesRef]
    var parentIds: [UUID]
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
    func ancestors(project: UUID, ids: [UUID]) async throws -> [BoardParentRef]
    func move(project: UUID, board: UUID, column: UUID, task: UUID, version: String) async throws -> BoardMoved
}

extension BoardViewSource {
    func ancestors(project: UUID, ids: [UUID]) async throws -> [BoardParentRef] { [] }
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

    func ancestors(project: UUID, ids: [UUID]) async throws -> [BoardParentRef] {
        var result: [BoardParentRef] = []
        var start = 0
        while start < ids.count {
            let chunk = Array(ids[start..<min(ids.count, start + 1000)])
            start += chunk.count
            let query = BoardTaskAncestorsQuery(projectId: project, ids: chunk)
            guard let found = try await client.graphQL.fetch(query).project?.taskAncestors else { continue }
            result += found.map { item in
                BoardParentRef(
                    id: item.id, title: item.title,
                    seriesNumbers: item.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) },
                    parentIds: item.parentIds)
            }
        }
        return result
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

struct BoardParentChain: Hashable, Sendable {
    var items: [BoardParentRef]

    var nearest: BoardParentRef { items[items.count - 1] }
}

struct BoardParentChains: Hashable, Sendable {
    static let maxShown = 2
    static let maxDepth = 10

    var chains: [BoardParentChain]
    var more: Int

    static let none = BoardParentChains(chains: [], more: 0)

    static func build(parentIds: [UUID], refs: [UUID: BoardParentRef]) -> BoardParentChains {
        var all: [BoardParentChain] = []
        for id in parentIds {
            guard let first = refs[id] else { continue }
            var items = [first]
            var visited: Set<UUID> = [first.id]
            var current = first
            while items.count < maxDepth, let next = current.parentIds.compactMap({ refs[$0] }).first(where: { !visited.contains($0.id) }) {
                items.append(next)
                visited.insert(next.id)
                current = next
            }
            all.append(BoardParentChain(items: items.reversed()))
        }
        return BoardParentChains(chains: Array(all.prefix(maxShown)), more: max(0, all.count - maxShown))
    }
}
