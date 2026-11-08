import Foundation
import Observation
import TaskerAPI

enum AdminPage: String, CaseIterable, Identifiable, Sendable {
    case profile
    case agents
    case problems

    var id: String { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .profile: .adminPageProfile
        case .agents: .adminPageAgents
        case .problems: .adminPageProblems
        }
    }
}

struct UserNameStore: Sendable {
    var file: WorkspaceSettingsFile
    var systemName: String

    init(file: WorkspaceSettingsFile = WorkspaceSettingsFile(directory: DaemonPaths().dataDirectory), systemName: String = Self.currentSystemName()) {
        self.file = file
        self.systemName = systemName
    }

    static func currentSystemName() -> String { normalized(NSUserName()) }

    static func normalized(_ name: String?) -> String {
        var text = (name ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if let slash = text.lastIndex(of: "\\") { text = String(text[text.index(after: slash)...]) }
        if let at = text.firstIndex(of: "@"), at != text.startIndex { text = String(text[..<at]) }
        return text.isEmpty ? "user" : text
    }

    func configured() throws -> String? { try file.userName() }

    func current() throws -> String { try configured() ?? systemName }

    func set(_ name: String?) throws { try file.setUserName(name) }
}

@MainActor
@Observable
final class AdminModel {
    private(set) var phase: SettingsPhase = .loading
    private(set) var agents: [AdminAgent] = []
    private(set) var mcp: AdminMcp?
    private(set) var problems = AdminProblems()
    private(set) var userName = ""
    private(set) var configuredName: String?
    private(set) var busy = false

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored private let source: any AdminSource
    @ObservationIgnored private let names: UserNameStore
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var generation = 0

    init(source: any AdminSource, names: UserNameStore = UserNameStore()) {
        self.source = source
        self.names = names
        refreshName()
    }

    var problemCount: Int { problems.count }

    func load() {
        generation += 1
        let current = generation
        loadTask?.cancel()
        loadTask = Task { [weak self] in await self?.fetch(current) }
    }

    func reload() async {
        generation += 1
        await fetch(generation)
    }

    func stop() {
        generation += 1
        loadTask?.cancel()
        loadTask = nil
    }

    private func fetch(_ current: Int) async {
        refreshName()
        do {
            async let loadedAgents = source.agents()
            async let loadedMcp = source.mcp()
            async let loadedProblems = source.problems()
            let (newAgents, newMcp, newProblems) = try await (loadedAgents, loadedMcp, loadedProblems)
            guard current == generation else { return }
            agents = newAgents
            mcp = newMcp
            problems = newProblems
            phase = .ready
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            if phase != .ready { phase = .failed(localizer.message(for: error)) }
        }
    }

    private func refreshName() {
        configuredName = (try? names.configured()) ?? nil
        userName = configuredName ?? names.systemName
    }

    var systemName: String { names.systemName }

    func saveName(_ text: String) -> String? {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.count > WorkspaceSettingsFile.maxUserNameLength {
            return localizer.string(.adminProfileTooLong, String(WorkspaceSettingsFile.maxUserNameLength))
        }
        do {
            try names.set(value.isEmpty ? nil : value)
        } catch {
            return localizer.string(.adminProfileSaveFailed, error.localizedDescription)
        }
        refreshName()
        onNotice?(localizer.string(.adminProfileSaved, userName))
        return nil
    }

    func resetName() -> String? { saveName("") }

    func createAgent(_ name: String) async -> String? {
        await run(.adminAgentCreated, name) { try await source.createAgent(name: name) }
    }

    func renameAgent(_ agent: AdminAgent, to name: String) async -> String? {
        await run(.adminAgentRenamed, name) { try await source.renameAgent(agent, name: name) }
    }

    func deleteAgent(_ agent: AdminAgent) async -> String? {
        await run(.adminAgentDeleted, agent.name) { try await source.deleteAgent(agent) }
    }

    func setRights(_ agent: AdminAgent, _ rights: AgentRightsValue) async -> String? {
        await run(.adminRightsSaved, agent.name) { try await source.setRights(agent, rights: rights) }
    }

    func cleanInvalidLinks(project: UUID) async -> String? {
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        let result: AdminCleanupResult
        do {
            result = try await source.cleanup(project: project)
        } catch {
            return SettingsModel.failure(error, l: localizer)
        }
        onNotice?(
            result.skipped
                ? localizer.string(.adminCleanupSkipped)
                : result.changes == 0 ? localizer.string(.adminCleanupNothing) : localizer.string(.adminCleanupDone, String(result.changes)))
        await reload()
        return nil
    }

    private func run(_ notice: L10nKey, _ argument: String, _ action: () async throws -> Void) async -> String? {
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        do {
            try await action()
        } catch {
            return SettingsModel.failure(error, l: localizer)
        }
        onNotice?(localizer.string(notice, argument))
        await reload()
        return nil
    }
}
