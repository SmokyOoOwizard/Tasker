import Foundation
import TaskerAPI

struct DraftLink: Hashable, Sendable, Identifiable {
    var typeId: UUID
    var outward: Bool
    var task: TaskLinkedBrief

    var id: String { "\(typeId.uuidString)/\(outward)/\(task.id.uuidString)" }
}

struct TaskCreateRequest: Hashable, Sendable {
    var title: String
    var description: String
    var typeId: UUID
    var statusId: UUID?
    var seriesIds: [UUID]
    var fields: [UUID: [String]]
    var fieldOrder: [UUID]
    var links: [DraftLink]
}

struct TaskCreated: Hashable, Sendable {
    var id: UUID
    var seriesNumbers: [TaskSeriesRef]
}

protocol TaskCreateSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func search(project: UUID) async throws -> [TaskLinkedBrief]
    func create(project: UUID, request: TaskCreateRequest) async throws -> TaskCreated
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLTaskCreateSource: TaskCreateSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project)
    }

    func search(project: UUID) async throws -> [TaskLinkedBrief] {
        try await GraphQLTaskDetailSource(client: client).search(project: project)
    }

    func create(project: UUID, request: TaskCreateRequest) async throws -> TaskCreated {
        var fields: Components.Schemas.CreateTask.FieldsPayload?
        let values = request.fieldOrder.compactMap { id in
            request.fields[id].map { Components.Schemas.TaskFieldValueInput(fieldId: id.wire, values: $0) }
        }
        if !values.isEmpty {
            fields = .init(value1: .init(values: values))
        }
        let links = request.links.map {
            Components.Schemas.NewTaskLink(typeId: $0.typeId.wire, taskId: $0.task.id.wire, direction: $0.outward ? .outward : .inward)
        }
        let body = Components.Schemas.CreateTask(
            title: request.title, description: request.description.isEmpty ? nil : request.description,
            typeId: request.typeId.wire, statusId: request.statusId?.wire,
            seriesIds: request.seriesIds.isEmpty ? nil : request.seriesIds.map(\.wire),
            fields: fields, links: links.isEmpty ? nil : links)
        let created = try await client.rest.perform {
            try await $0.createTask(path: .init(projectId: project.wire), body: .json(body)).created.body.json
        }
        guard let id = UUID(uuidString: created.id) else { throw TaskerAPIError.notFound(created.id) }
        return TaskCreated(id: id, seriesNumbers: created.seriesNumbers.map { TaskSeriesRef(seriesId: UUID(uuidString: $0.seriesId) ?? UUID(), number: Int($0.number)) })
    }
}
