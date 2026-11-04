import Foundation
import Observation
import TaskerAPI

struct WorkspaceTabsState: Codable, Equatable, Sendable {
    var initialized = false
    var open: [String] = []
    var selected: String?
    var recents: [String] = []
    var projects: [String: String] = [:]
}

@MainActor
@Observable
final class WorkspaceTabs {
    static let storageKey = "workspaceTabs"
    static let maxRecents = 8

    private(set) var state: WorkspaceTabsState

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let key: String

    init(defaults: UserDefaults = .standard, key: String = WorkspaceTabs.storageKey) {
        self.defaults = defaults
        self.key = key
        if let data = defaults.data(forKey: key), let stored = try? JSONDecoder().decode(WorkspaceTabsState.self, from: data) {
            state = stored
        } else {
            state = WorkspaceTabsState()
        }
    }

    var open: [String] { state.open }

    var selected: String? { state.selected }

    var recents: [String] {
        state.recents.filter { path in !state.open.contains(path) }
    }

    func seed(from daemonPaths: [String]) {
        guard !state.initialized else { return }
        state.initialized = true
        state.open = Self.unique(daemonPaths.map(WorkspacePath.canonical))
        state.selected = state.open.first
        save()
    }

    func open(_ path: String) {
        let path = WorkspacePath.canonical(path)
        if !state.open.contains(path) { state.open.append(path) }
        state.selected = path
        state.initialized = true
        remember(path)
        save()
    }

    func close(_ path: String) {
        guard let index = state.open.firstIndex(of: path) else { return }
        state.open.remove(at: index)
        remember(path)
        if state.selected == path {
            state.selected = state.open.isEmpty ? nil : state.open[min(index, state.open.count - 1)]
        }
        save()
    }

    func select(_ path: String) {
        guard state.open.contains(path), state.selected != path else { return }
        state.selected = path
        save()
    }

    func forget(recent path: String) {
        state.recents.removeAll { $0 == path }
        save()
    }

    func projectId(in path: String) -> UUID? {
        state.projects[path].flatMap(UUID.init(uuidString:))
    }

    func setProject(_ id: UUID?, in path: String) {
        state.projects[path] = id?.uuidString
        save()
    }

    private func remember(_ path: String) {
        state.recents.removeAll { $0 == path }
        state.recents.insert(path, at: 0)
        if state.recents.count > Self.maxRecents { state.recents.removeLast(state.recents.count - Self.maxRecents) }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(state) {
            defaults.set(data, forKey: key)
        }
    }

    private static func unique(_ paths: [String]) -> [String] {
        var seen = Set<String>()
        return paths.filter { seen.insert($0).inserted }
    }
}

enum WorkspaceTabStatus: Equatable, Sendable {
    case open(key: String)
    case opening
    case failed(String)
    case absent

    init(path: String, in status: DaemonStatus?) {
        guard let status, let workspace = status.workspaces.first(where: { WorkspacePath.same($0.path, path) }) else {
            self = .absent
            return
        }
        switch workspace.state {
        case "open":
            self = workspace.key.map { .open(key: $0) } ?? .opening
        case "opening":
            self = .opening
        default:
            self = .failed(workspace.error ?? "")
        }
    }
}

enum ProjectChoice {
    static func resolve(_ projects: [ProjectFields], stored: UUID?) -> ProjectFields? {
        projects.first { $0.id == stored } ?? projects.first
    }
}
