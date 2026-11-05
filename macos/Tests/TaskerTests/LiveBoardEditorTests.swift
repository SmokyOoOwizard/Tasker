import Foundation
import Testing
import TaskerAPI
@testable import Tasker

private enum LiveBoards {
    static var client: TaskerAPIClient? {
        let environment = ProcessInfo.processInfo.environment
        guard let port = environment["TASKER_LIVE_PORT"].flatMap(Int.init), let workspace = environment["TASKER_LIVE_WORKSPACE"] else { return nil }
        return TaskerAPIClient(endpoint: .local(port: port, workspace: workspace))
    }

    static func delete(_ client: TaskerAPIClient, project: UUID, id: UUID, version: String) async throws {
        try await client.rest.perform { client in
            _ = try await client.deleteBoard(path: .init(projectId: project.uuidString.lowercased(), id: id.uuidString.lowercased()), query: .init(version: version)).noContent
        }
    }

    static func cli(_ arguments: [String]) throws {
        guard let tool = ProcessInfo.processInfo.environment["TASKER_LIVE_CLI"] else { return }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        try process.run()
        process.waitUntilExit()
    }
}

@MainActor
@Suite(.enabled(if: LiveBoards.client != nil, "TASKER_LIVE_PORT and TASKER_LIVE_WORKSPACE point to a running daemon"))
struct LiveBoardEditorTests {
    func settle(_ check: @MainActor () -> Bool) async {
        for _ in 0..<600 {
            if check() { return }
            try? await Task.sleep(for: .milliseconds(10))
        }
    }

    @Test(.timeLimit(.minutes(2))) func createsEditsLocksAndRejects() async throws {
        let client = try #require(LiveBoards.client)
        let projects = try await client.graphQL.fetch(ProjectsQuery()).projects.data
        let project = try #require(projects.first).id
        let source = GraphQLBoardEditorSource(client: client)
        let l = Localizer(language: .en)

        let creator = BoardEditorModel(target: .init(project: project, board: nil), source: source)
        creator.localizer = l
        creator.load()
        await settle { creator.phase == .ready }
        #expect(creator.draft.columns.count == 4)
        creator.draft.name = "Live " + UUID().uuidString.prefix(6)
        creator.edit { draft, catalog in
            draft.columns[1].conditions = [catalog.newCondition()!]
        }
        var created = false
        creator.onCreated = { created = true }
        let ok = await creator.save()
        print("LIVE create issues:", creator.issues)
        #expect(ok)
        #expect(created)

        let boards = try await client.graphQL.fetch(TaskListVocabularyQuery(projectId: project)).project?.boards.data ?? []
        let id = try #require(boards.first { $0.name == creator.draft.name.trimmingCharacters(in: .whitespaces) }).id

        let editor = BoardEditorModel(target: .init(project: project, board: id), source: source, heartbeat: .milliseconds(300))
        editor.localizer = l
        editor.load()
        await settle { editor.lock == .mine }
        #expect(editor.lock == .mine)
        #expect(editor.draft.columns[1].conditions.count == 1)
        #expect(editor.draft.columns[0].statusIds.count == 1)

        editor.edit { draft, _ in
            BoardEditing.move(column: 3, by: -1, in: &draft)
            draft.columns[0].name = "Renamed"
        }
        #expect(await editor.save())
        #expect(editor.draft.columns[0].name == "Renamed")
        #expect(editor.draft.columns[2].name == creator.draft.columns[3].name)

        let shared = editor.draft.columns[0].statusIds[0]
        editor.edit { draft, _ in draft.columns[3].statusIds.append(shared) }
        #expect(await editor.save() == false)
        let rejected = editor.draft.columns[3].id
        #expect(editor.issues.columns[rejected]?.first?.hasPrefix("Board rejected: status") == true)
        print("LIVE overlap message:", editor.issues.columns[rejected]?.first ?? "")

        editor.edit { draft, catalog in
            var api = catalog.newCondition()!
            api.op = .equal
            var other = api
            other.op = .notEqual
            draft.columns[0].conditions = [api]
            draft.columns[3].conditions = [other]
        }
        #expect(await editor.save())
        #expect(editor.draft.columns[3].statusIds.contains(shared))

        editor.release()
        try await Task.sleep(for: .milliseconds(400))
        let free = try await source.lock(project: project, id: id)
        #expect(free == .none)

        try LiveBoards.cli(["lock", "acquire", "board", id.uuidString])
        let blocked = BoardEditorModel(target: .init(project: project, board: id), source: source)
        blocked.localizer = l
        blocked.load()
        await settle { blocked.holder != nil }
        print("LIVE holder:", blocked.holder ?? "nil")
        #expect(blocked.holder != nil)
        #expect(!blocked.canSave)
        try LiveBoards.cli(["lock", "release", "board", id.uuidString])
        blocked.refreshLock()
        await settle { blocked.lock == .mine }
        #expect(blocked.lock == .mine)
        blocked.release()
        try await Task.sleep(for: .milliseconds(300))

        let last = try #require(try await source.board(project: project, id: id))
        try await LiveBoards.delete(client, project: project, id: id, version: last.version)
    }
}
