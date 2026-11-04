import Foundation
import Observation

enum TaskListMode: String, CaseIterable, Codable, Identifiable, Sendable {
    case groups
    case flat
    case tree

    var id: String { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .groups: .taskListViewGroups
        case .flat: .taskListViewFlat
        case .tree: .taskListViewTree
        }
    }
}

struct TaskSortKey: Hashable, Codable, Sendable {
    var key: String
    var descending: Bool

    var term: String { (descending ? "-" : "") + key }
}

enum TaskSortField: Hashable, Sendable {
    case status
    case type
    case title
    case created
    case updated
    case series
    case field(String)

    static let builtIn: [TaskSortField] = [.status, .type, .title, .created, .updated, .series]

    var key: String {
        switch self {
        case .status: "status"
        case .type: "type"
        case .title: "title"
        case .created: "created"
        case .updated: "updated"
        case .series: "series"
        case .field(let name): name
        }
    }

    init(key: String) {
        self = Self.builtIn.first { $0.key == key } ?? .field(key)
    }

    func title(_ l: Localizer) -> String {
        switch self {
        case .status: l.string(.taskListSortStatus)
        case .type: l.string(.taskListSortType)
        case .title: l.string(.taskListSortTitle)
        case .created: l.string(.taskListSortCreated)
        case .updated: l.string(.taskListSortUpdated)
        case .series: l.string(.taskListSortSeries)
        case .field(let name): name
        }
    }

    static func isSortable(fieldName: String) -> Bool {
        !fieldName.isEmpty && fieldName.allSatisfy { $0.isLetter || $0.isNumber }
    }
}

enum TaskSort {
    static func string(_ keys: [TaskSortKey]) -> String? {
        keys.isEmpty ? nil : keys.map(\.term).joined(separator: ",")
    }

    static func cycled(_ keys: [TaskSortKey], _ key: String) -> [TaskSortKey] {
        var result = keys
        if let index = result.firstIndex(where: { $0.key == key }) {
            if result[index].descending {
                result.remove(at: index)
            } else {
                result[index].descending = true
            }
        } else {
            result.append(TaskSortKey(key: key, descending: false))
        }
        return result
    }

    static func toggled(_ keys: [TaskSortKey], _ key: String) -> [TaskSortKey] {
        keys.map { $0.key == key ? TaskSortKey(key: $0.key, descending: !$0.descending) : $0 }
    }

    static func removed(_ keys: [TaskSortKey], _ key: String) -> [TaskSortKey] {
        keys.filter { $0.key != key }
    }
}

struct TaskListColumns: Hashable, Codable, Sendable {
    var key = true
    var type = false
    var fields = true
    var updated = true
    var fieldIds: [UUID]?
}

struct TaskListFilter: Hashable, Codable, Sendable {
    var statusIds: [UUID]?
    var typeIds: [UUID]?
    var seriesIds: [UUID]?
    var field: [String]?

    static let none = TaskListFilter()
}

struct TaskListPreferences: Hashable, Codable, Sendable {
    var mode: TaskListMode = .groups
    var sort: [TaskSortKey] = []
    var columns = TaskListColumns()
    var expanded: [UUID] = []
    var collapsedGroups: [UUID] = []

    static let maxExpanded = 500

    init() {}

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        mode = (try? container.decode(TaskListMode.self, forKey: .mode)) ?? .groups
        sort = (try? container.decode([TaskSortKey].self, forKey: .sort)) ?? []
        columns = (try? container.decode(TaskListColumns.self, forKey: .columns)) ?? TaskListColumns()
        expanded = (try? container.decode([UUID].self, forKey: .expanded)) ?? []
        collapsedGroups = (try? container.decode([UUID].self, forKey: .collapsedGroups)) ?? []
    }

    mutating func setExpanded(_ id: UUID, _ value: Bool) {
        expanded.removeAll { $0 == id }
        if value { expanded.append(id) }
        if expanded.count > Self.maxExpanded { expanded.removeFirst(expanded.count - Self.maxExpanded) }
    }

    mutating func setCollapsed(group id: UUID, _ value: Bool) {
        collapsedGroups.removeAll { $0 == id }
        if value { collapsedGroups.append(id) }
    }
}

@MainActor
@Observable
final class TaskListSettings {
    static let storagePrefix = "taskList."

    private(set) var revision = 0
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private var cache: [UUID: TaskListPreferences] = [:]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func preferences(for project: UUID) -> TaskListPreferences {
        _ = revision
        if let cached = cache[project] { return cached }
        let stored = defaults.data(forKey: Self.storagePrefix + project.uuidString.lowercased())
            .flatMap { try? JSONDecoder().decode(TaskListPreferences.self, from: $0) } ?? TaskListPreferences()
        cache[project] = stored
        return stored
    }

    func update(_ project: UUID, _ change: (inout TaskListPreferences) -> Void) {
        var value = preferences(for: project)
        change(&value)
        guard value != cache[project] else { return }
        cache[project] = value
        revision += 1
        if let data = try? JSONEncoder().encode(value) {
            defaults.set(data, forKey: Self.storagePrefix + project.uuidString.lowercased())
        }
    }
}
