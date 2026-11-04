import Foundation
import TaskerAPI

struct ProjectServiceError: Error, Equatable, LocalizedError {
    var message: String

    var errorDescription: String? { message }
}

enum ProjectNameCheck: Equatable {
    case ok(String)
    case empty
    case duplicate

    init(_ raw: String, existing: [String]) {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if name.isEmpty {
            self = .empty
        } else if existing.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) {
            self = .duplicate
        } else {
            self = .ok(name)
        }
    }
}

struct ProjectService: Sendable {
    let client: TaskerAPIClient

    @discardableResult
    func create(name: String) async throws -> UUID {
        let project = try await client.rest.perform { try await $0.createProject(body: .json(.init(name: name))).created.body.json }
        _ = try? await client.graphQL.fetch(ProjectsQuery(limit: 200))
        guard let id = UUID(uuidString: project.id) else { throw ProjectServiceError(message: project.id) }
        return id
    }
}

enum OnboardingStep: Int, CaseIterable, Identifiable, Sendable {
    case statuses
    case statusSet
    case taskType
    case board

    var id: Int { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .statuses: .onboardingStatusesTitle
        case .statusSet: .onboardingSetTitle
        case .taskType: .onboardingTypeTitle
        case .board: .onboardingBoardTitle
        }
    }

    var hintKey: L10nKey {
        switch self {
        case .statuses: .onboardingStatusesHint
        case .statusSet: .onboardingSetHint
        case .taskType: .onboardingTypeHint
        case .board: .onboardingBoardHint
        }
    }

    var doneKey: L10nKey {
        switch self {
        case .statuses: .onboardingStatusesDone
        case .statusSet: .onboardingSetDone
        case .taskType: .onboardingTypeDone
        case .board: .onboardingBoardDone
        }
    }

    func isDone(_ stats: ProjectQuery.Data.Project.Stats) -> Bool {
        switch self {
        case .statuses: stats.statuses > 0
        case .statusSet: stats.statusSets > 0
        case .taskType: stats.taskTypes > 0
        case .board: stats.boards > 0
        }
    }

    func isUnlocked(_ stats: ProjectQuery.Data.Project.Stats) -> Bool {
        switch self {
        case .statuses: true
        case .statusSet: stats.statuses > 0
        case .taskType: stats.statusSets > 0
        case .board: stats.statusSets > 0 && stats.statuses > 0
        }
    }

    static func isEmpty(_ stats: ProjectQuery.Data.Project.Stats) -> Bool {
        stats.tasks == 0 && allCases.contains { !$0.isDone(stats) }
    }

    static func completed(_ stats: ProjectQuery.Data.Project.Stats) -> Int {
        allCases.filter { $0.isDone(stats) }.count
    }
}

struct OnboardingNames: Sendable {
    var todo: String
    var doing: String
    var done: String
    var set: String
    var type: String
    var board: String

    init(_ l: Localizer) {
        todo = l.string(.onboardingNameTodo)
        doing = l.string(.onboardingNameDoing)
        done = l.string(.onboardingNameDone)
        set = l.string(.onboardingNameSet)
        type = l.string(.onboardingNameType)
        board = l.string(.onboardingNameBoard)
    }
}

struct ProjectOnboarding: Sendable {
    let client: TaskerAPIClient
    let projectId: UUID
    let names: OnboardingNames

    func run(_ step: OnboardingStep) async throws {
        let project = projectId.uuidString
        switch step {
        case .statuses:
            for (name, color) in [(names.todo, "#2684ff"), (names.doing, "#f5a623"), (names.done, "#36b37e")] {
                _ = try await client.rest.perform {
                    try await $0.createStatus(path: .init(projectId: project), body: .json(.init(name: name, color: color))).created.body.json
                }
            }
        case .statusSet:
            let setup = try await setup()
            _ = try await client.rest.perform {
                try await $0.createStatusSet(path: .init(projectId: project), body: .json(.init(name: names.set, statusIds: setup.statuses.map(\.id)))).created.body.json
            }
        case .taskType:
            let setup = try await setup()
            guard let set = setup.statusSets.first else { throw ProjectServiceError(message: names.set) }
            _ = try await client.rest.perform {
                try await $0.createTaskType(path: .init(projectId: project), body: .json(.init(name: names.type, statusSetId: set))).created.body.json
            }
        case .board:
            let setup = try await setup()
            guard let set = setup.statusSets.first else { throw ProjectServiceError(message: names.set) }
            let columns = setup.statuses.map { Components.Schemas.BoardColumnInput(name: $0.name, statusIds: [$0.id]) }
            _ = try await client.rest.perform {
                try await $0.createBoard(path: .init(projectId: project), body: .json(.init(name: names.board, statusSetIds: [set], columns: columns))).created.body.json
            }
        }
        _ = try? await client.graphQL.fetch(ProjectQuery(id: projectId))
    }

    private func setup() async throws -> (statuses: [(id: String, name: String)], statusSets: [String]) {
        let data = try await client.graphQL.fetch(ProjectSetupQuery(projectId: projectId))
        guard let project = data.project else { throw ProjectServiceError(message: projectId.uuidString) }
        return (project.statuses.data.map { (id: $0.id.uuidString.lowercased(), name: $0.name) }, project.statusSets.data.map { $0.id.uuidString.lowercased() })
    }
}
