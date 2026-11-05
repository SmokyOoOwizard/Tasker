import Foundation
import TaskerAPI

protocol SeriesSource: Sendable {
    func overview(project: UUID, series: UUID) async throws -> SeriesOverview?
    func stats(project: UUID, series: UUID) async throws -> SeriesStats
    func conflictTask(project: UUID, task: UUID) async throws -> SeriesConflictTask?
    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws
    func renumber(project: UUID, task: UUID, series: UUID, to number: Int, version: String) async throws
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLSeriesSource: SeriesSource {
    static let page = 200
    static let cap = 20_000

    let client: TaskerAPIClient

    func overview(project: UUID, series: UUID) async throws -> SeriesOverview? {
        guard let data = try await client.graphQL.fetch(SeriesOverviewQuery(projectId: project, id: series)).project else {
            throw TaskerAPIError.notFound(project.uuidString)
        }
        guard let found = data.series else { return nil }
        return SeriesOverview(
            info: SeriesInfo(id: found.id, name: found.name, prefix: found.prefix, version: found.version),
            all: data.seriesList.data.map { SeriesBrief(id: $0.id, name: $0.name, prefix: $0.prefix) },
            conflicts: data.seriesHealth.numberConflicts.map { SeriesNumberConflict(seriesId: $0.seriesId, number: $0.number, taskIds: $0.taskIds) })
    }

    func stats(project: UUID, series: UUID) async throws -> SeriesStats {
        var offset = 0
        var maxNumber = 0
        var total = 0
        while offset < Self.cap {
            guard let page = try await client.graphQL.fetch(
                SeriesNumbersPageQuery(projectId: project, seriesIds: [series], offset: offset, limit: Self.page)).project?.tasks
            else { throw TaskerAPIError.notFound(project.uuidString) }
            total = page.totalCount
            for task in page.data {
                for number in task.seriesNumbers where number.seriesId == series { maxNumber = max(maxNumber, number.number) }
            }
            offset += page.data.count
            if page.data.isEmpty || offset >= page.totalCount { break }
        }
        return SeriesStats(count: total, maxNumber: maxNumber)
    }

    func conflictTask(project: UUID, task: UUID) async throws -> SeriesConflictTask? {
        guard let found = try await client.graphQL.fetch(SeriesConflictTaskQuery(projectId: project, id: task)).project?.task else { return nil }
        return SeriesConflictTask(
            id: found.id, title: found.title, createdAt: found.createdAt, version: found.version,
            numbers: found.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) })
    }

    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateSeries(
                path: .init(projectId: project.wire, id: series.wire),
                body: .json(.init(name: name, prefix: prefix, version: version))
            ).ok.body.json
        }
    }

    func renumber(project: UUID, task: UUID, series: UUID, to number: Int, version: String) async throws {
        _ = try await client.rest.perform {
            try await $0.renumberTask(
                path: .init(projectId: project.wire, id: task.wire, seriesId: series.wire),
                body: .json(.init(to: Int32(number), version: version))
            ).ok.body.json
        }
    }
}
