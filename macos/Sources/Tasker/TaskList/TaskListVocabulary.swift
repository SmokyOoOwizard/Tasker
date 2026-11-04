import Foundation
import TaskerAPI

struct TaskRowField: Hashable, Sendable {
    var fieldId: UUID
    var values: [String]
    var ownName: String?
    var ownType: FieldType?
    var ownEnumId: UUID?
}

struct TaskRow: Hashable, Sendable {
    var id: UUID
    var title: String
    var typeId: UUID
    var statusId: UUID
    var seriesNumbers: [TaskSeriesRef]
    var createdAt: Date
    var updatedAt: Date
    var version: String
    var parentIds: [UUID]
    var childCount: Int
    var fields: [TaskRowField]
    var repeated = false
    var treeChildCount = 0
}

struct TaskSeriesRef: Hashable, Sendable {
    var seriesId: UUID
    var number: Int
}

extension TaskRow {
    init(_ item: TaskRowFields) {
        self.init(
            id: item.id, title: item.title, typeId: item.typeId, statusId: item.statusId,
            seriesNumbers: item.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) },
            createdAt: item.createdAt, updatedAt: item.updatedAt, version: item.version,
            parentIds: item.parentIds, childCount: item.childCount,
            fields: item.fields.map { TaskRowField(fieldId: $0.fieldId, values: $0.values, ownName: $0.own?.name, ownType: $0.own?.type, ownEnumId: $0.own?.enumId) })
    }

    init(_ item: TaskTreeRowFields) {
        self.init(
            id: item.id, title: item.title, typeId: item.typeId, statusId: item.statusId,
            seriesNumbers: item.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) },
            createdAt: item.createdAt, updatedAt: item.updatedAt, version: item.version,
            parentIds: item.parentIds, childCount: item.childCount,
            fields: item.fields.map { TaskRowField(fieldId: $0.fieldId, values: $0.values, ownName: $0.own?.name, ownType: $0.own?.type, ownEnumId: $0.own?.enumId) },
            repeated: item.repeated, treeChildCount: item.treeChildCount)
    }
}

enum StatusGlyph: Hashable, Sendable {
    case named(StatusIconName)
    case progress(Double)

    var primitives: [StatusIconPrimitive] {
        switch self {
        case .named(let name):
            return name.primitives
        case .progress(let fraction):
            return [
                StatusIconPrimitive(shape: .circle(cx: 7, cy: 7, r: 5.5), paint: StatusIconPaint(strokeWidth: DesignLayout.iconStroke)),
                StatusIconPrimitive(shape: .pie(cx: 7, cy: 7, r: 5.5, fraction: CGFloat(fraction)), paint: StatusIconPaint(fill: true))
            ]
        }
    }
}

struct TaskChip: Hashable, Sendable {
    var text: String
    var colorHex: String
}

struct TaskListVocabulary: Hashable, Sendable {
    struct Status: Hashable, Sendable {
        var id: UUID
        var name: String
        var color: String
    }

    struct TaskType: Hashable, Sendable {
        var name: String
        var statusSetId: UUID
    }

    struct Field: Hashable, Sendable {
        var id: UUID
        var name: String
        var type: FieldType
        var multiple: Bool
        var enumId: UUID?
    }

    struct EnumValue: Hashable, Sendable {
        var id: String
        var name: String
    }

    var statuses: [Status] = []
    var statusSets: [UUID: [UUID]] = [:]
    var types: [UUID: TaskType] = [:]
    var typeOrder: [UUID] = []
    var prefixes: [UUID: String] = [:]
    var fields: [Field] = []
    var enums: [UUID: [EnumValue]] = [:]

    static let empty = TaskListVocabulary()

    private var statusById: [UUID: Status] {
        Dictionary(statuses.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
    }

    func status(_ id: UUID) -> Status? {
        statuses.first { $0.id == id }
    }

    var orderedStatuses: [Status] {
        var place: [UUID: Int] = [:]
        for ids in statusSets.values {
            for (index, id) in ids.enumerated() { place[id] = min(place[id] ?? index, index) }
        }
        return statuses.enumerated().sorted { lhs, rhs in
            let left = place[lhs.element.id] ?? Int.max
            let right = place[rhs.element.id] ?? Int.max
            return left == right ? lhs.offset < rhs.offset : left < right
        }.map(\.element)
    }

    func statusOptions(forType typeId: UUID) -> [Status] {
        guard let set = types[typeId].flatMap({ statusSets[$0.statusSetId] }) else { return statuses }
        let byId = statusById
        return set.compactMap { byId[$0] }
    }

    func glyph(status statusId: UUID, type typeId: UUID) -> StatusGlyph {
        let ids = types[typeId].flatMap { statusSets[$0.statusSetId] } ?? statuses.map(\.id)
        guard let index = ids.firstIndex(of: statusId) else { return .named(.dashed) }
        if index == 0 { return .named(.dashed) }
        let last = ids.count - 1
        if last > 0, index >= last { return .named(.done) }
        return .progress(last > 0 ? Double(index) / Double(last) : 0)
    }

    func glyph(status statusId: UUID) -> StatusGlyph {
        let type = typeOrder.first { types[$0].flatMap { statusSets[$0.statusSetId] }?.contains(statusId) ?? false }
            ?? types.first { statusSets[$0.value.statusSetId]?.contains(statusId) ?? false }?.key
        guard let type else { return .named(.dashed) }
        return glyph(status: statusId, type: type)
    }

    func reference(_ row: TaskRow) -> String {
        for number in row.seriesNumbers {
            if let prefix = prefixes[number.seriesId] { return "\(prefix)-\(number.number)" }
        }
        return "#" + row.id.uuidString.lowercased().prefix(8)
    }

    var defaultChipFields: [UUID] {
        fields.filter { $0.type == .enum }.map(\.id)
    }

    func chips(_ row: TaskRow, fieldIds: [UUID]?, limit: Int, l: Localizer) -> [TaskChip] {
        let order = fieldIds ?? defaultChipFields
        let byField = Dictionary(row.fields.map { ($0.fieldId, $0) }, uniquingKeysWith: { first, _ in first })
        let definitions = Dictionary(fields.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var chips: [TaskChip] = []
        for id in order {
            guard let value = byField[id], let field = definitions[id] else { continue }
            chips += fieldChips(field, values: value.values, l: l)
            if chips.count >= limit { break }
        }
        return Array(chips.prefix(limit))
    }

    private func fieldChips(_ field: Field, values: [String], l: Localizer) -> [TaskChip] {
        switch field.type {
        case .enum:
            let options = field.enumId.flatMap { enums[$0] } ?? []
            return values.map { value in
                let index = options.firstIndex { $0.id.caseInsensitiveCompare(value) == .orderedSame }
                let name = index.map { options[$0].name } ?? value
                return TaskChip(text: name, colorHex: DesignTag.palette[((index ?? 0) + 1) % DesignTag.palette.count])
            }
        case .bool:
            return values.contains("true") ? [TaskChip(text: field.name, colorHex: DesignTag.palette[0])] : []
        case .date:
            return values.compactMap { TaskerDate.parse($0) ?? Self.day($0) }.map { TaskChip(text: l.shortDate($0), colorHex: DesignTag.palette[0]) }
        case .int, .float:
            return values.map { TaskChip(text: "\(field.name) \($0)", colorHex: DesignTag.palette[0]) }
        case .string:
            return values.map { TaskChip(text: $0, colorHex: DesignTag.palette[0]) }
        }
    }

    private static func day(_ text: String) -> Date? {
        try? Date(text, strategy: .iso8601.year().month().day())
    }

    func sortFields() -> [TaskSortField] {
        TaskSortField.builtIn + fields.map(\.name).filter(TaskSortField.isSortable).map { TaskSortField.field($0) }
    }
}

extension TaskListVocabulary {
    init(_ project: TaskListVocabularyQuery.Data.Project) {
        statuses = project.statuses.data.map { Status(id: $0.id, name: $0.name, color: $0.color) }
        statusSets = Dictionary(project.statusSets.data.map { ($0.id, $0.statusIds) }, uniquingKeysWith: { first, _ in first })
        types = Dictionary(project.taskTypes.data.map { ($0.id, TaskType(name: $0.name, statusSetId: $0.statusSetId)) }, uniquingKeysWith: { first, _ in first })
        typeOrder = project.taskTypes.data.map(\.id)
        prefixes = Dictionary(project.seriesList.data.map { ($0.id, $0.prefix) }, uniquingKeysWith: { first, _ in first })
        fields = project.fields.data.map { Field(id: $0.id, name: $0.name, type: $0.type, multiple: $0.multiple, enumId: $0.enumId) }
        enums = Dictionary(project.enums.data.map { ($0.id, $0.values.map { EnumValue(id: $0.id.uuidString, name: $0.name) }) }, uniquingKeysWith: { first, _ in first })
    }
}
