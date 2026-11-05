import Foundation
import TaskerAPI

struct BoardCondition: Hashable, Sendable, Identifiable {
    var id = UUID()
    var fieldId: UUID
    var op: FieldOperator
    var value: String

    static func == (lhs: BoardCondition, rhs: BoardCondition) -> Bool {
        lhs.fieldId == rhs.fieldId && lhs.op == rhs.op && lhs.value == rhs.value
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(fieldId)
        hasher.combine(op)
        hasher.combine(value)
    }

    var takesValue: Bool {
        switch op {
        case .set, .unset, .attached, .detached: false
        default: true
        }
    }
}

struct BoardColumnDraft: Hashable, Sendable, Identifiable {
    var id = UUID()
    var serverId: UUID?
    var name: String
    var statusIds: [UUID] = []
    var drop: [UUID: UUID] = [:]
    var conditions: [BoardCondition] = []

    static func == (lhs: BoardColumnDraft, rhs: BoardColumnDraft) -> Bool {
        lhs.serverId == rhs.serverId && lhs.name == rhs.name && lhs.statusIds == rhs.statusIds && lhs.drop == rhs.drop
            && lhs.conditions == rhs.conditions
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(serverId)
        hasher.combine(name)
        hasher.combine(statusIds)
        hasher.combine(conditions)
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

struct BoardFieldInfo: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var type: FieldType
    var multiple: Bool
    var enumId: UUID?
}

struct BoardEnumValue: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
}

struct BoardCatalog: Hashable, Sendable {
    var statuses: [BoardStatusInfo] = []
    var sets: [BoardSetInfo] = []
    var fields: [BoardFieldInfo] = []
    var enums: [UUID: [BoardEnumValue]] = [:]

    func status(_ id: UUID) -> BoardStatusInfo? { statuses.first { $0.id == id } }
    func set(_ id: UUID) -> BoardSetInfo? { sets.first { $0.id == id } }
    func field(_ id: UUID) -> BoardFieldInfo? { fields.first { $0.id == id } }

    func statusIds(inSets ids: [UUID]) -> [UUID] {
        var seen = Set<UUID>()
        var result: [UUID] = []
        for id in ids {
            for status in set(id)?.statusIds ?? [] where seen.insert(status).inserted { result.append(status) }
        }
        return result
    }

    func operators(for field: BoardFieldInfo) -> [FieldOperator] {
        var result: [FieldOperator] = [.equal, .notEqual]
        if [.int, .float, .date].contains(field.type) { result += [.greater, .greaterOrEqual, .less, .lessOrEqual] }
        return result + [.set, .unset, .attached, .detached]
    }

    func defaultValue(for field: BoardFieldInfo) -> String {
        switch field.type {
        case .enum: field.enumId.flatMap { enums[$0]?.first?.id.uuidString.lowercased() } ?? ""
        case .bool: "true"
        default: ""
        }
    }

    func newCondition() -> BoardCondition? {
        guard let field = fields.first(where: { $0.type == .enum }) ?? fields.first else { return nil }
        return BoardCondition(fieldId: field.id, op: .equal, value: defaultValue(for: field))
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

    static func changeField(_ condition: inout BoardCondition, to field: BoardFieldInfo, catalog: BoardCatalog) {
        condition.fieldId = field.id
        if !catalog.operators(for: field).contains(condition.op) { condition.op = .equal }
        condition.value = condition.takesValue ? catalog.defaultValue(for: field) : ""
    }

    static func changeOperator(_ condition: inout BoardCondition, to op: FieldOperator, field: BoardFieldInfo?, catalog: BoardCatalog) {
        condition.op = op
        if !condition.takesValue {
            condition.value = ""
        } else if condition.value.isEmpty, let field {
            condition.value = catalog.defaultValue(for: field)
        }
    }

    static func symbol(_ op: FieldOperator) -> String {
        switch op {
        case .equal: "="
        case .notEqual: "!="
        case .greater: ">"
        case .greaterOrEqual: ">="
        case .less: "<"
        case .lessOrEqual: "<="
        case .set: ":set"
        case .unset: ":unset"
        case .attached: ":attached"
        case .detached: ":detached"
        }
    }

    static func filterText(_ condition: BoardCondition, catalog: BoardCatalog) -> String? {
        guard let field = catalog.field(condition.fieldId) else { return nil }
        return field.name + symbol(condition.op) + (condition.takesValue ? condition.value.trimmingCharacters(in: .whitespaces) : "")
    }
}

struct BoardIssues: Equatable {
    var board: [String] = []
    var columns: [UUID: [String]] = [:]
    var conditions: [UUID: String] = [:]

    var isEmpty: Bool { board.isEmpty && columns.isEmpty && conditions.isEmpty }
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
            for condition in column.conditions where condition.takesValue && condition.value.trimmingCharacters(in: .whitespaces).isEmpty {
                issues.conditions[condition.id] = l.string(.boardConditionValueRequired)
            }
        }
        return issues
    }
}

enum BoardServerIssue {
    private static let overlap = try! NSRegularExpression(
        pattern: #"^Columns\[(\d+)\]\.StatusIds: status ([0-9a-fA-F-]{36}) is already used by column '(.*?)', and"#, options: [.dotMatchesLineSeparators])
    private static let indexed = try! NSRegularExpression(pattern: #"^Columns\[(\d+)\]"#)

    static func map(_ message: String, draft: BoardDraft, catalog: BoardCatalog, l: Localizer) -> BoardIssues {
        var issues = BoardIssues()
        let range = NSRange(message.startIndex..., in: message)
        func text(_ match: NSTextCheckingResult, _ group: Int) -> String? {
            Range(match.range(at: group), in: message).map { String(message[$0]) }
        }
        if let match = overlap.firstMatch(in: message, range: range),
           let index = text(match, 1).flatMap({ Int($0) }), draft.columns.indices.contains(index) {
            let status = text(match, 2).flatMap { UUID(uuidString: $0) }.flatMap { catalog.status($0)?.name } ?? ""
            let first = text(match, 3) ?? ""
            issues.columns[draft.columns[index].id] = [l.string(.boardOverlap, status, first, draft.columns[index].name)]
        } else if let match = indexed.firstMatch(in: message, range: range),
                  let index = text(match, 1).flatMap({ Int($0) }), draft.columns.indices.contains(index) {
            issues.columns[draft.columns[index].id] = [message]
        } else {
            issues.board = [message]
        }
        return issues
    }
}
