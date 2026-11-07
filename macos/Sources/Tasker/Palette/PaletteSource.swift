import Foundation
import TaskerAPI

protocol PaletteSource: Sendable {
    func vocabulary(project: UUID) async throws -> TaskListVocabulary
    func search(project: UUID, text: String, limit: Int) async throws -> [PaletteTaskHit]
}

struct GraphQLPaletteSource: PaletteSource {
    let client: TaskerAPIClient

    func vocabulary(project: UUID) async throws -> TaskListVocabulary {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project)
    }

    func search(project: UUID, text: String, limit: Int) async throws -> [PaletteTaskHit] {
        guard let hits = try await client.graphQL.fetch(TaskPaletteSearchQuery(projectId: project, query: text, limit: limit)).project?.taskSearch else { return [] }
        return hits.map { hit in
            PaletteTaskHit(
                brief: TaskLinkedBrief(
                    id: hit.task.id, title: hit.task.title, statusId: hit.task.statusId, typeId: hit.task.typeId,
                    seriesNumbers: hit.task.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) }),
                score: hit.score,
                isExactNumber: hit.field == .number && hit.match == .exact,
                snippet: hit.snippet.map { snippet in
                    PaletteSnippet(text: snippet.text, ranges: snippet.ranges.map { $0.start..<($0.start + $0.length) })
                })
        }
    }
}
