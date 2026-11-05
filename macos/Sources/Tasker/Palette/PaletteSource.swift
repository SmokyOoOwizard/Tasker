import Foundation
import TaskerAPI

protocol PaletteSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func search(project: UUID, text: String, limit: Int) async throws -> [TaskLinkedBrief]
    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief]
}

struct GraphQLPaletteSource: PaletteSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project)
    }

    func search(project: UUID, text: String, limit: Int) async throws -> [TaskLinkedBrief] {
        guard let page = try await client.graphQL.fetch(TaskPaletteSearchQuery(projectId: project, search: text, limit: limit)).project?.tasks else { return [] }
        return page.data.map {
            TaskLinkedBrief(
                id: $0.id, title: $0.title, statusId: $0.statusId, typeId: $0.typeId,
                seriesNumbers: $0.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) })
        }
    }

    func resolve(project: UUID, ref: String) async throws -> [TaskLinkedBrief] {
        try await GraphQLTaskDetailSource(client: client).resolve(project: project, ref: ref)
    }
}
