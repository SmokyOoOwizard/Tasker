import Foundation
import TaskerAPI

struct BoardColumnDraft: Hashable, Sendable, Identifiable {
    var id = UUID()
    var serverId: UUID?
    var name: String
    var statusIds: [UUID] = []
    var drop: [UUID: UUID] = [:]
    var query = ""

    static func == (lhs: BoardColumnDraft, rhs: BoardColumnDraft) -> Bool {
        lhs.serverId == rhs.serverId && lhs.name == rhs.name && lhs.statusIds == rhs.statusIds && lhs.drop == rhs.drop
            && lhs.trimmedQuery == rhs.trimmedQuery
    }

    var trimmedQuery: String { query.trimmingCharacters(in: .whitespacesAndNewlines) }

    func hash(into hasher: inout Hasher) {
        hasher.combine(serverId)
        hasher.combine(name)
        hasher.combine(statusIds)
        hasher.combine(trimmedQuery)
    }
}

struct BoardDraft: Hashable, Sendable {
    var name = ""
    var setIds: [UUID] = []
    var columns: [BoardColumnDraft] = []
}

struct BoardRecord: Hashable, Sendable {
    var id: UUID
    var version: String
    var draft: BoardDraft
}

struct BoardSetInfo: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var statusIds: [UUID]
}

struct BoardStatusInfo: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var color: String
}

struct BoardCatalog: Hashable, Sendable {
    var statuses: [BoardStatusInfo] = []
    var sets: [BoardSetInfo] = []

    func status(_ id: UUID) -> BoardStatusInfo? { statuses.first { $0.id == id } }
    func set(_ id: UUID) -> BoardSetInfo? { sets.first { $0.id == id } }

    func statusIds(inSets ids: [UUID]) -> [UUID] {
        var seen = Set<UUID>()
        var result: [UUID] = []
        for id in ids {
            for status in set(id)?.statusIds ?? [] where seen.insert(status).inserted { result.append(status) }
        }
        return result
    }
}

enum BoardEditing {
    static func initial(_ catalog: BoardCatalog) -> BoardDraft {
        guard let first = catalog.sets.first else { return BoardDraft() }
        let columns = first.statusIds.map { id in
            BoardColumnDraft(name: catalog.status(id)?.name ?? "", statusIds: [id])
        }
        return BoardDraft(name: "", setIds: [first.id], columns: columns)
    }

    static func toggleSet(_ id: UUID, in draft: inout BoardDraft, catalog: BoardCatalog) {
        if let index = draft.setIds.firstIndex(of: id) {
            draft.setIds.remove(at: index)
        } else {
            draft.setIds.append(id)
        }
        let allowed = Set(catalog.statusIds(inSets: draft.setIds))
        let sets = Set(draft.setIds)
        for index in draft.columns.indices {
            draft.columns[index].statusIds.removeAll { !allowed.contains($0) }
            draft.columns[index].drop = draft.columns[index].drop.filter {
                sets.contains($0.key) && draft.columns[index].statusIds.contains($0.value)
            }
        }
    }

    static func toggleStatus(_ status: UUID, column: Int, in draft: inout BoardDraft) {
        guard draft.columns.indices.contains(column) else { return }
        if let index = draft.columns[column].statusIds.firstIndex(of: status) {
            draft.columns[column].statusIds.remove(at: index)
            draft.columns[column].drop = draft.columns[column].drop.filter { $0.value != status }
        } else {
            draft.columns[column].statusIds.append(status)
        }
    }

    static func setDrop(_ status: UUID?, set: UUID, column: Int, in draft: inout BoardDraft) {
        guard draft.columns.indices.contains(column) else { return }
        draft.columns[column].drop[set] = status
    }

    static func move(column: Int, by offset: Int, in draft: inout BoardDraft) {
        let target = column + offset
        guard draft.columns.indices.contains(column), draft.columns.indices.contains(target) else { return }
        draft.columns.swapAt(column, target)
    }
}

struct BoardIssues: Equatable {
    var board: [String] = []
    var columns: [UUID: [String]] = [:]

    var isEmpty: Bool { board.isEmpty && columns.isEmpty }
}

enum BoardValidation {
    static func check(_ draft: BoardDraft, catalog: BoardCatalog, l: Localizer) -> BoardIssues {
        var issues = BoardIssues()
        if draft.name.trimmingCharacters(in: .whitespaces).isEmpty { issues.board.append(l.string(.boardNameRequired)) }
        if draft.setIds.isEmpty { issues.board.append(l.string(.boardSetRequired)) }
        if draft.columns.isEmpty { issues.board.append(l.string(.boardColumnRequired)) }
        for column in draft.columns {
            if column.name.trimmingCharacters(in: .whitespaces).isEmpty {
                issues.columns[column.id, default: []].append(l.string(.boardColumnNameRequired))
            }
        }
        return issues
    }
}

enum BoardServerIssue {
    static func map(_ refusal: Refusal, draft: BoardDraft, l: Localizer) -> BoardIssues {
        var issues = BoardIssues()
        let text = l.refusal(refusal)
        if let index = refusal.columnIndex, draft.columns.indices.contains(index) {
            issues.columns[draft.columns[index].id] = [text]
        } else {
            issues.board = [text]
        }
        return issues
    }
}
