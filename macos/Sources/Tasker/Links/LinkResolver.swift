import Foundation
import TaskerAPI

struct LinkProject: Equatable, Sendable {
    var id: UUID
    var name: String
}

struct LinkCandidate: Equatable, Sendable, Identifiable {
    enum Destination: Equatable, Sendable {
        case task(UUID)
        case board(UUID)
        case series(UUID)
    }

    var project: LinkProject
    var title: String
    var destination: Destination

    var id: String {
        switch destination {
        case .task(let id): "task:" + id.uuidString
        case .board(let id): "board:" + id.uuidString
        case .series(let id): "series:" + id.uuidString
        }
    }
}

enum LinkResolution: Equatable, Sendable {
    case found([LinkCandidate])
    case notFound
    case failed(String)
}

protocol LinkSource: Sendable {
    func tasks(project: UUID, reference: String) async throws -> [(id: UUID, title: String)]
    func task(project: UUID, id: UUID) async throws -> String?
    func series(project: UUID, prefix: String) async throws -> (id: UUID, name: String)?
    func board(project: UUID, id: UUID) async throws -> String?
}

enum LinkResolver {
    static func resolve(_ target: TaskerLinkTarget, projects: [LinkProject], source: any LinkSource) async -> LinkResolution {
        var found: [LinkCandidate] = []
        var failure: String?
        for project in projects {
            do {
                found += try await candidates(target, project: project, source: source)
            } catch {
                failure = failure ?? error.localizedDescription
            }
        }
        if !found.isEmpty { return .found(found) }
        if let failure { return .failed(failure) }
        return .notFound
    }

    private static func candidates(_ target: TaskerLinkTarget, project: LinkProject, source: any LinkSource) async throws -> [LinkCandidate] {
        switch target {
        case .workspace:
            return []
        case .task(let reference):
            return try await source.tasks(project: project.id, reference: reference).map {
                LinkCandidate(project: project, title: $0.title, destination: .task($0.id))
            }
        case .taskID(let id):
            guard let title = try await source.task(project: project.id, id: id) else { return [] }
            return [LinkCandidate(project: project, title: title, destination: .task(id))]
        case .board(let id):
            guard let name = try await source.board(project: project.id, id: id) else { return [] }
            return [LinkCandidate(project: project, title: name, destination: .board(id))]
        case .series(let prefix):
            guard let series = try await source.series(project: project.id, prefix: prefix) else { return [] }
            return [LinkCandidate(project: project, title: series.name, destination: .series(series.id))]
        }
    }
}

struct GraphQLLinkSource: LinkSource {
    let client: TaskerAPIClient

    func tasks(project: UUID, reference: String) async throws -> [(id: UUID, title: String)] {
        try await GraphQLTaskDetailSource(client: client).resolve(project: project, ref: reference).map { ($0.id, $0.title) }
    }

    func task(project: UUID, id: UUID) async throws -> String? {
        do {
            return try await GraphQLTaskDetailSource(client: client).load(project: project, task: id)?.title
        } catch TaskerAPIError.notFound(_) {
            return nil
        }
    }

    func series(project: UUID, prefix: String) async throws -> (id: UUID, name: String)? {
        let vocabulary = try await GraphQLTaskListSource(client: client).vocabulary(project: project)
        guard let match = vocabulary.prefixes.first(where: { $0.value.caseInsensitiveCompare(prefix) == .orderedSame }) else { return nil }
        return (match.key, vocabulary.seriesNames[match.key] ?? match.value)
    }

    func board(project: UUID, id: UUID) async throws -> String? {
        try await GraphQLTaskListSource(client: client).vocabulary(project: project).boardNames[id]
    }
}
