import Foundation
import TaskerAPI

protocol TaskDetailSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func load(project: UUID, task: UUID) async throws -> TaskDetailData?
    func update(project: UUID, task: UUID, request: TaskUpdateRequest) async throws
    func addLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws
    func removeLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws
    func addToSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws
    func removeFromSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws
    func renumber(project: UUID, task: UUID, series: UUID, to number: Int?, version: String?) async throws
    func delete(project: UUID, task: UUID, version: String?) async throws
    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief]
    func search(project: UUID) async throws -> [TaskLinkedBrief]
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLTaskDetailSource: TaskDetailSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project)
    }

    func load(project: UUID, task: UUID) async throws -> TaskDetailData? {
        guard let data = try await client.graphQL.fetch(TaskQuery(projectId: project, id: task)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        return data.task.map(TaskDetailData.init)
    }

    func update(project: UUID, task: UUID, request: TaskUpdateRequest) async throws {
        var fields: Components.Schemas.UpdateTask.FieldsPayload?
        if request.hasFieldChanges {
            let values = request.setOrder.compactMap { id in
                request.setValues[id].map { Components.Schemas.TaskFieldValueInput(fieldId: id.wire, values: $0) }
            }
            let own = request.newOwnFields.map {
                Components.Schemas.NewOwnField(
                    name: $0.name, _type: Components.Schemas.FieldType(rawValue: $0.type == .enum ? "enum" : $0.type.rawValue) ?? .string,
                    values: $0.values.isEmpty ? nil : $0.values, required: $0.required, multiple: $0.multiple, enumId: $0.enumId?.wire)
            }
            fields = .init(value1: .init(
                values: values.isEmpty ? nil : values,
                addFields: request.addFields.isEmpty ? nil : request.addFields.map(\.wire),
                newOwnFields: own.isEmpty ? nil : own,
                removeFields: request.removeFields.isEmpty ? nil : request.removeFields.map(\.wire)))
        }
        let body = Components.Schemas.UpdateTask(
            title: request.title, description: request.description, typeId: request.typeId?.wire,
            statusId: request.statusId?.wire, version: request.version, fields: fields)
        _ = try await client.rest.perform {
            try await $0.updateTask(path: .init(projectId: project.wire, id: task.wire), body: .json(body)).ok.body.json
        }
    }

    func addLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.addTaskLink(
                path: .init(projectId: project.wire, id: task.wire),
                body: .json(.init(typeId: type.wire, targetId: target.wire, version: version))
            ).ok.body.json
        }
    }

    func removeLink(project: UUID, task: UUID, type: UUID, target: UUID, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.removeTaskLink(
                path: .init(projectId: project.wire, id: task.wire),
                query: .init(typeId: type.wire, targetId: target.wire, version: version)
            ).ok.body.json
        }
    }

    func addToSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.addTaskToSeries(
                path: .init(projectId: project.wire, id: task.wire),
                body: .json(.init(seriesId: series.wire, version: version))
            ).ok.body.json
        }
    }

    func removeFromSeries(project: UUID, task: UUID, series: UUID, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.removeTaskFromSeries(
                path: .init(projectId: project.wire, id: task.wire, seriesId: series.wire),
                query: .init(version: version)
            ).ok.body.json
        }
    }

    func renumber(project: UUID, task: UUID, series: UUID, to number: Int?, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.renumberTask(
                path: .init(projectId: project.wire, id: task.wire, seriesId: series.wire),
                body: .json(.init(to: number.map { Int32($0) }, version: version))
            ).ok.body.json
        }
    }

    func delete(project: UUID, task: UUID, version: String?) async throws {
        _ = try await client.rest.perform {
            try await $0.deleteTask(
                path: .init(projectId: project.wire, id: task.wire),
                query: .init(version: version)
            ).noContent
        }
    }

    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief] {
        guard let data = try await client.graphQL.fetch(ResolveTaskRefQuery(projectId: project, ref: ref)).project else { return [] }
        return data.resolveTasks.map {
            TaskLinkedBrief(
                id: $0.id, title: $0.title, statusId: $0.statusId, typeId: $0.typeId,
                seriesNumbers: $0.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) })
        }
    }

    func search(project: UUID) async throws -> [TaskLinkedBrief] {
        var result: [TaskLinkedBrief] = []
        var offset = 0
        while offset < Self.searchCap {
            guard let page = try await client.graphQL.fetch(TaskSearchPageQuery(projectId: project, offset: offset, limit: Self.searchPage)).project?.tasks else { break }
            result += page.data.map {
                TaskLinkedBrief(
                    id: $0.id, title: $0.title, statusId: $0.statusId, typeId: $0.typeId,
                    seriesNumbers: $0.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) })
            }
            offset += page.data.count
            if page.data.isEmpty || offset >= page.totalCount { break }
        }
        return result
    }

    static let searchPage = 200
    static let searchCap = 2000
}
