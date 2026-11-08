import Foundation
import TaskerAPI

typealias BoardLockState = EditLockState

protocol BoardEditorSource: Sendable {
    func catalog(project: UUID) async throws -> BoardCatalog
    func board(project: UUID, id: UUID) async throws -> BoardRecord?
    func create(project: UUID, draft: BoardDraft, catalog: BoardCatalog) async throws -> UUID
    func update(project: UUID, record: BoardRecord, draft: BoardDraft, catalog: BoardCatalog) async throws
    func lock(project: UUID, id: UUID) async throws -> BoardLockState
    func acquire(project: UUID, id: UUID) async throws
    func release(project: UUID, id: UUID) async throws
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLBoardEditorSource: BoardEditorSource {
    let client: TaskerAPIClient

    func catalog(project: UUID) async throws -> BoardCatalog {
        guard let data = try await client.graphQL.fetch(BoardEditorCatalogQuery(projectId: project)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        var catalog = BoardCatalog()
        catalog.statuses = data.statuses.data.map { BoardStatusInfo(id: $0.id, name: $0.name, color: $0.color) }
        catalog.sets = data.statusSets.data.map { BoardSetInfo(id: $0.id, name: $0.name, statusIds: $0.statusIds) }
        return catalog
    }

    func board(project: UUID, id: UUID) async throws -> BoardRecord? {
        guard let data = try await client.graphQL.fetch(BoardEditorBoardQuery(projectId: project, id: id)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        guard let board = data.board else { return nil }
        let columns = board.columns.map { column in
            BoardColumnDraft(
                serverId: column.id, name: column.name, statusIds: column.statusIds,
                drop: Dictionary(column.dropStatuses.map { ($0.statusSetId, $0.statusId) }, uniquingKeysWith: { first, _ in first }),
                query: column.query ?? "")
        }
        return BoardRecord(id: board.id, version: board.version, draft: BoardDraft(name: board.name, setIds: board.statusSetIds, columns: columns))
    }

    func create(project: UUID, draft: BoardDraft, catalog: BoardCatalog) async throws -> UUID {
        let body = Components.Schemas.CreateBoard(
            name: draft.name.trimmingCharacters(in: .whitespaces), statusSetIds: draft.setIds.map(\.wire),
            columns: draft.columns.map { Self.input($0, original: nil, catalog: catalog) })
        let created = try await client.rest.perform {
            try await $0.createBoard(path: .init(projectId: project.wire), body: .json(body)).created.body.json
        }
        guard let id = UUID(uuidString: created.id) else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: created.id)) }
        return id
    }

    func update(project: UUID, record: BoardRecord, draft: BoardDraft, catalog: BoardCatalog) async throws {
        let originals = Dictionary(record.draft.columns.compactMap { column in column.serverId.map { ($0, column) } }, uniquingKeysWith: { first, _ in first })
        let body = Components.Schemas.UpdateBoard(
            name: draft.name.trimmingCharacters(in: .whitespaces), statusSetIds: draft.setIds.map(\.wire),
            columns: draft.columns.map { Self.input($0, original: $0.serverId.flatMap { originals[$0] }, catalog: catalog) },
            version: record.version)
        _ = try await client.rest.perform {
            try await $0.updateBoard(path: .init(projectId: project.wire, id: record.id.wire), body: .json(body)).ok.body.json
        }
    }

    func lock(project: UUID, id: UUID) async throws -> BoardLockState {
        guard let data = try await client.graphQL.fetch(BoardLockQuery(projectId: project, entity: .board, id: id)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        guard let lock = data.lock else { return .none }
        return lock.mine ? .mine : .other(lock.holder)
    }

    func acquire(project: UUID, id: UUID) async throws {
        _ = try await client.rest.perform {
            try await $0.acquireBoardLock(path: .init(projectId: project.wire, id: id.wire)).ok.body.json
        }
    }

    func release(project: UUID, id: UUID) async throws {
        _ = try await client.rest.perform {
            try await $0.releaseBoardLock(path: .init(projectId: project.wire, id: id.wire)).noContent
        }
    }

    static func input(_ column: BoardColumnDraft, original: BoardColumnDraft?, catalog: BoardCatalog) -> Components.Schemas.BoardColumnInput {
        let drop = Dictionary(column.drop.map { ($0.key.wire, $0.value.wire) }, uniquingKeysWith: { first, _ in first })
        let text = column.trimmedQuery
        let query: String? = original.map { $0.trimmedQuery == text } == true || (original == nil && text.isEmpty) ? nil : text
        return .init(
            id: column.serverId?.wire, name: column.name.trimmingCharacters(in: .whitespaces), statusIds: column.statusIds.map(\.wire),
            dropStatuses: .init(additionalProperties: drop), query: query)
    }
}
