import Foundation
import TaskerAPI

struct TaskLinkedBrief: Hashable, Sendable, Identifiable {
    var id: UUID
    var title: String
    var statusId: UUID
    var typeId: UUID?
    var seriesNumbers: [TaskSeriesRef]
}

struct TaskFieldValue: Hashable, Sendable, Identifiable {
    var fieldId: UUID
    var name: String
    var type: FieldType
    var multiple: Bool
    var enumId: UUID?
    var required: Bool
    var source: TaskFieldSource
    var values: [String]
    var texts: [String]

    var id: UUID { fieldId }

    var isEmpty: Bool { values.allSatisfy { $0.trimmingCharacters(in: .whitespaces).isEmpty } }
}

struct TaskLinkEntry: Hashable, Sendable, Identifiable {
    var typeId: UUID
    var typeName: String
    var direction: LinkDirection
    var name: String
    var task: TaskLinkedBrief

    var id: String { "\(typeId.uuidString)/\(direction.rawValue)/\(task.id.uuidString)" }
}

struct TaskDetailData: Hashable, Sendable {
    var id: UUID
    var projectId: UUID
    var title: String
    var description: String
    var typeId: UUID
    var statusId: UUID
    var seriesNumbers: [TaskSeriesRef]
    var createdAt: Date
    var updatedAt: Date
    var version: String
    var fields: [TaskFieldValue]
    var links: [TaskLinkEntry]
}

extension TaskDetailData {
    init(_ task: TaskQuery.Data.Project.Task) {
        self.init(
            id: task.id, projectId: task.projectId, title: task.title, description: task.description ?? "",
            typeId: task.typeId, statusId: task.statusId,
            seriesNumbers: task.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) },
            createdAt: task.createdAt, updatedAt: task.updatedAt, version: task.version,
            fields: task.fieldViews.map {
                TaskFieldValue(
                    fieldId: $0.fieldId, name: $0.name, type: $0.type, multiple: $0.multiple, enumId: $0.enumId,
                    required: $0.required, source: $0.source, values: $0.values, texts: $0.texts)
            },
            links: task.linkViews.map {
                TaskLinkEntry(
                    typeId: $0.typeId, typeName: $0.typeName, direction: $0.direction, name: $0.name,
                    task: TaskLinkedBrief(
                        id: $0.task.id, title: $0.task.title, statusId: $0.task.statusId, typeId: nil,
                        seriesNumbers: $0.task.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) }))
            })
    }
}

struct NewOwnFieldInput: Hashable, Sendable {
    var name: String
    var type: FieldType
    var required: Bool
    var multiple: Bool
    var enumId: UUID?
    var values: [String]
}

enum TaskEdit: Hashable, Sendable {
    case title(String)
    case description(String)
    case status(UUID)
    case type(UUID, status: UUID?)
    case setField(UUID, [String])
    case addFields([UUID])
    case addOwnField(NewOwnFieldInput)
    case removeField(UUID)
    case addSeries(UUID)
    case removeSeries(UUID)
    case renumber(UUID, Int?)
    case addLink(type: UUID, target: UUID, outward: Bool)
    case removeLink(type: UUID, target: UUID, outward: Bool)
    case delete
}

struct TaskUpdateRequest: Hashable, Sendable {
    var title: String?
    var description: String?
    var typeId: UUID?
    var statusId: UUID?
    var setValues: [UUID: [String]] = [:]
    var setOrder: [UUID] = []
    var addFields: [UUID] = []
    var newOwnFields: [NewOwnFieldInput] = []
    var removeFields: [UUID] = []
    var version: String?

    var hasFieldChanges: Bool {
        !setValues.isEmpty || !addFields.isEmpty || !newOwnFields.isEmpty || !removeFields.isEmpty
    }
}

extension TaskListVocabulary {
    func reference(_ numbers: [TaskSeriesRef], id: UUID) -> String {
        for number in numbers {
            if let prefix = prefixes[number.seriesId] { return "\(prefix)-\(number.number)" }
        }
        return "#" + id.uuidString.lowercased().prefix(8)
    }

    func series(_ number: TaskSeriesRef) -> String {
        "\(prefixes[number.seriesId] ?? "?")-\(number.number)"
    }

    func linkType(_ id: UUID) -> LinkType? {
        linkTypes.first { $0.id == id }
    }

    func isDone(status: UUID) -> Bool {
        glyph(status: status) == .named(.done)
    }

    func requiredFields(forType typeId: UUID) -> Set<UUID> {
        Set((types[typeId]?.fields ?? []).filter(\.required).map(\.fieldId))
    }

    func catalogFields(excluding present: Set<UUID>) -> [Field] {
        fields.filter { !present.contains($0.id) }
    }

    func enumValues(_ id: UUID?) -> [EnumValue] {
        id.flatMap { enums[$0] } ?? []
    }

    func statusNeedsChoice(current: UUID, newType: UUID) -> Bool {
        !statusOptions(forType: newType).contains { $0.id == current }
    }

    func seriesAvailable(excluding used: [TaskSeriesRef]) -> [UUID] {
        let taken = Set(used.map(\.seriesId))
        return seriesOrder.filter { !taken.contains($0) }
    }
}
