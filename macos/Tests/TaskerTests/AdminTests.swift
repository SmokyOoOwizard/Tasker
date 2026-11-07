import Foundation
import Testing
import TaskerAPI
@testable import Tasker

private func scratchDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "tasker-admin-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

final class StubAdminSource: AdminSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _calls: [String] = []
    var agentsValue: [AdminAgent] = []
    var mcpValue = AdminMcp(daemon: .running, port: 5719, url: "http://127.0.0.1:5719/mcp", workspace: "demo", workspaceState: .open, error: nil, agentHeader: "X-Tasker-Agent")
    var problemsValue = AdminProblems()
    var failure: TaskerAPIError?
    var cleanupValue = AdminCleanupResult(changes: 2, skipped: false)

    var calls: [String] { lock.withLock { _calls } }
    private func record(_ text: String) { lock.withLock { _calls.append(text) } }

    func agents() async throws -> [AdminAgent] { agentsValue }
    func mcp() async throws -> AdminMcp { mcpValue }
    func problems() async throws -> AdminProblems { problemsValue }

    func createAgent(name: String) async throws {
        if let failure { throw failure }
        record("create \(name)")
        agentsValue.append(AdminAgent(id: UUID(), name: name, version: "1"))
    }

    func renameAgent(_ agent: AdminAgent, name: String) async throws {
        record("rename \(agent.name)->\(name)")
        agentsValue = agentsValue.map { $0.id == agent.id ? AdminAgent(id: $0.id, name: name, version: "2") : $0 }
    }

    func deleteAgent(_ agent: AdminAgent) async throws {
        record("delete \(agent.name)")
        agentsValue.removeAll { $0.id == agent.id }
    }

    func cleanup(project: UUID) async throws -> AdminCleanupResult {
        record("cleanup")
        return cleanupValue
    }
}

@MainActor
private func loadedModel(_ source: StubAdminSource, directory: URL, system: String = "ivan") async -> AdminModel {
    let names = UserNameStore(file: WorkspaceSettingsFile(directory: directory), systemName: system)
    let model = AdminModel(source: source, names: names)
    await model.reload()
    return model
}

@Suite struct UserNameStoreTests {
    @Test func fallsBackToSystemNameAndStoresOverride() throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = UserNameStore(file: WorkspaceSettingsFile(directory: directory), systemName: "ivan")

        #expect(try store.current() == "ivan")
        try store.set("  Иван П.  ")
        #expect(try store.current() == "Иван П.")
        try store.set(nil)
        #expect(try store.current() == "ivan")
        #expect(try store.configured() == nil)
    }

    @Test func keepsOtherSettings() throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let folder = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = WorkspaceSettingsFile(directory: directory)
        try file.add(folder.path)
        try file.setUserName("Anna")

        #expect(try file.workspacePaths() == [WorkspacePath.canonical(folder.path)])
        let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: file.file)) as? [String: Any])
        #expect(root["userName"] as? String == "Anna")

        try file.setUserName("")
        let after = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: file.file)) as? [String: Any])
        #expect(after["userName"] == nil)
        #expect(try file.workspacePaths().count == 1)
    }

    @Test func rejectsTooLongName() throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = WorkspaceSettingsFile(directory: directory)

        #expect(throws: WorkspaceSettingsError.self) { try file.setUserName(String(repeating: "a", count: 101)) }
        try file.setUserName(String(repeating: "a", count: 100))
        #expect(try file.userName()?.count == 100)
    }

    @Test func normalizesSystemName() {
        #expect(UserNameStore.normalized("DOMAIN\\ivan") == "ivan")
        #expect(UserNameStore.normalized("ivan@corp.local") == "ivan")
        #expect(UserNameStore.normalized("  ") == "user")
    }
}

@Suite @MainActor struct AdminModelTests {
    @Test func profileNameRoundTrip() async throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let model = await loadedModel(StubAdminSource(), directory: directory)

        #expect(model.userName == "ivan")
        #expect(model.saveName("Мария") == nil)
        #expect(model.userName == "Мария")
        #expect(model.configuredName == "Мария")
        #expect(model.resetName() == nil)
        #expect(model.userName == "ivan")
        #expect(model.saveName(String(repeating: "x", count: 101)) != nil)
        #expect(model.userName == "ivan")
    }

    @Test func agentLifecycleReloadsList() async throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = StubAdminSource()
        let model = await loadedModel(source, directory: directory)

        #expect(await model.createAgent("reviewer") == nil)
        let created = try #require(model.agents.first)
        #expect(created.name == "reviewer")
        #expect(await model.renameAgent(created, to: "bot") == nil)
        #expect(model.agents.first?.name == "bot")
        #expect(await model.deleteAgent(try #require(model.agents.first)) == nil)
        #expect(model.agents.isEmpty)
        #expect(source.calls == ["create reviewer", "rename reviewer->bot", "delete bot"])
    }

    @Test func failureIsReturnedAndListKept() async throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = StubAdminSource()
        source.failure = .notFound(Refusal(code: "entity.notFound", message: "x"))
        let model = await loadedModel(source, directory: directory)

        #expect(await model.createAgent("x") != nil)
        #expect(model.agents.isEmpty)
    }

    @Test func problemCountAndActions() async throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let project = UUID()
        let series = UUID()
        let source = StubAdminSource()
        source.problemsValue = AdminProblems(items: [
            AdminProblem(.unreadableFile(path: "tasks/a.yaml", error: "bad")),
            AdminProblem(.secondFolder(path: "projects/x")),
            AdminProblem(.numberConflict(AdminNumberConflict(project: project, projectName: "P", series: series, prefix: "TSK", number: 5, taskCount: 2))),
            AdminProblem(.numberConflict(AdminNumberConflict(project: project, projectName: "P", series: series, prefix: "TSK", number: 6, taskCount: 2))),
            AdminProblem(.invalidLinks(project: project, name: "P", count: 3)),
            AdminProblem(.linkCycle(AdminLinkCycle(projectName: "P", typeName: "blocks", path: "TSK-1 → TSK-2 → TSK-1")))
        ])
        let model = await loadedModel(source, directory: directory)

        #expect(model.problemCount == 6)
        #expect(model.problems.projectsWithConflicts.count == 1)
        #expect(model.problems.projectsWithInvalidLinks.map(\.project) == [project])
    }

    @Test func cleanupReportsAndReloads() async throws {
        let directory = try scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = StubAdminSource()
        let model = await loadedModel(source, directory: directory)
        var notices: [String] = []
        model.onNotice = { notices.append($0) }

        #expect(await model.cleanInvalidLinks(project: UUID()) == nil)
        source.cleanupValue = AdminCleanupResult(changes: 0, skipped: true)
        #expect(await model.cleanInvalidLinks(project: UUID()) == nil)
        #expect(notices.count == 2)
        #expect(notices[0] != notices[1])
        #expect(source.calls == ["cleanup", "cleanup"])
    }

    @Test func mcpCommandForAgentAndLocal() {
        let mcp = AdminMcp(daemon: .running, port: 5719, url: "http://127.0.0.1:5719/mcp", workspace: "demo", workspaceState: .open, error: nil, agentHeader: "X-Tasker-Agent")
        let agent = UUID()

        #expect(mcp.command(agent: nil) == "claude mcp add --transport http tasker http://127.0.0.1:5719/mcp")
        #expect(mcp.command(agent: agent) == "claude mcp add --transport http tasker http://127.0.0.1:5719/mcp --header \"X-Tasker-Agent: \(agent.uuidString.lowercased())\"")
        #expect(mcp.isReady)
        var stopped = mcp
        stopped.daemon = .stopped
        stopped.url = nil
        #expect(!stopped.isReady)
        #expect(stopped.command(agent: nil) == nil)
    }

    @Test func pagesAreProfileAgentsProblems() {
        #expect(AdminPage.allCases.map(\.rawValue) == ["profile", "agents", "problems"])
    }
}
