import Foundation
import TaskerAPI

protocol SeriesSource: Sendable {
    func overview(project: UUID, series: UUID) async throws -> SeriesOverview?
    func stats(project: UUID, series: UUID) async throws -> SeriesStats
    func plan(project: UUID, series: UUID) async throws -> SeriesConflictPlan
    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws
    func resolve(project: UUID, series: UUID, token: String) async throws
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
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
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
            else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString)) }
            total = page.totalCount
            for task in page.data {
                for number in task.seriesNumbers where number.seriesId == series { maxNumber = max(maxNumber, number.number) }
            }
            offset += page.data.count
            if page.data.isEmpty || offset >= page.totalCount { break }
        }
        return SeriesStats(count: total, maxNumber: maxNumber)
    }

    func plan(project: UUID, series: UUID) async throws -> SeriesConflictPlan {
        guard let plan = try await client.graphQL.fetch(SeriesConflictPlanQuery(projectId: project, seriesId: series)).project?.seriesConflictPlan else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: series.uuidString))
        }
        return SeriesConflictPlan(
            token: plan.token,
            steps: plan.steps.map {
                SeriesPlanStep(taskId: $0.taskId, title: $0.taskTitle, from: $0.oldNumber, to: $0.newNumber, keeperId: $0.keeperId, keeperTitle: $0.keeperTitle)
            })
    }

    func update(project: UUID, series: UUID, name: String, prefix: String, version: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateSeries(
                path: .init(projectId: project.wire, id: series.wire),
                body: .json(.init(name: name, prefix: prefix, version: version))
            ).ok.body.json
        }
    }

    func resolve(project: UUID, series: UUID, token: String) async throws {
        _ = try await client.rest.perform {
            try await $0.resolveSeriesConflicts(
                path: .init(projectId: project.wire, id: series.wire),
                body: .json(.init(token: token))
            ).ok.body.json
        }
    }
}
