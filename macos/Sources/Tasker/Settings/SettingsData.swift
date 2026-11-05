import Foundation
import TaskerAPI

enum ProjectMode: Equatable, Sendable {
    case local
    case server

    init(wire: String) {
        self = wire.lowercased() == "server" ? .server : .local
    }
}

struct SettingsFieldItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var type: FieldType
    var multiple: Bool
    var enumId: UUID?
    var version: String
}

struct SettingsEnumValue: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
}

struct SettingsEnumItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var values: [SettingsEnumValue]
    var version: String
}

struct SettingsLinkTypeItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var outward: String
    var inward: String
    var allowCycles: Bool
    var hierarchical: Bool
    var version: String

    var isDirected: Bool { !inward.isEmpty }
}

struct SettingsSeriesItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var prefix: String
    var version: String
}

struct SettingsTypeItem: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var fieldIds: [UUID]
}

struct SettingsBoardCondition: Hashable, Sendable {
    var board: String
    var column: String
    var fieldId: UUID
    var value: String?
}

struct SettingsHealth: Hashable, Sendable {
    var numberConflicts: [SeriesNumberConflict] = []
    var prefixConflicts: [String] = []
    var invalidTasks = 0
    var unreadableFiles = 0

    var isClean: Bool { numberConflicts.isEmpty && prefixConflicts.isEmpty && invalidTasks == 0 && unreadableFiles == 0 }
}

struct SettingsProjectItem: Hashable, Sendable {
    var id: UUID
    var name: String
    var createdAt: Date
    var version: String
}

struct SettingsCatalog: Hashable, Sendable {
    var project: SettingsProjectItem
    var fields: [SettingsFieldItem] = []
    var enums: [SettingsEnumItem] = []
    var linkTypes: [SettingsLinkTypeItem] = []
    var series: [SettingsSeriesItem] = []
    var types: [SettingsTypeItem] = []
    var conditions: [SettingsBoardCondition] = []
    var health = SettingsHealth()

    func field(_ id: UUID?) -> SettingsFieldItem? { fields.first { $0.id == id } }
    func enumeration(_ id: UUID?) -> SettingsEnumItem? { enums.first { $0.id == id } }
    func fields(of enumeration: UUID) -> [SettingsFieldItem] { fields.filter { $0.enumId == enumeration } }
}

struct SettingsTaskBrief: Hashable, Sendable, Identifiable {
    var id: UUID
    var title: String
    var numbers: [TaskSeriesRef]
    var fields: [UUID: [String]]

    func values(of field: UUID) -> [String] { fields[field] ?? [] }
}

struct SettingsTaskPage: Hashable, Sendable {
    var total: Int
    var items: [SettingsTaskBrief]

    var isComplete: Bool { items.count >= total }
}

struct SettingsSeriesStat: Hashable, Sendable {
    var count: Int
    var maxNumber: Int

    var next: Int { maxNumber + 1 }
}

struct SettingsMember: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var email: String?
    var isAgent: Bool
    var isAdmin: Bool
    var ownerId: UUID?
}

enum SettingsLockTarget: Hashable, Sendable {
    case project
    case field(UUID)
    case enumeration(UUID)
    case linkType(UUID)
    case series(UUID)
}

enum FieldValueChoice: Hashable, Sendable {
    case clear
    case map
    case keepFirst
}

struct FieldChangeRequest: Hashable, Sendable {
    var clearUnconvertible = true
    var keepFirst: Bool?
    var mapping: [String: String] = [:]
}

enum EnumRemovalChoice: Hashable, Sendable {
    case clear
    case reassign(UUID)
}

struct EnumValueDraft: Hashable, Identifiable, Sendable {
    var key = UUID()
    var serverId: UUID?
    var name: String

    var id: UUID { key }
}

enum SettingsRules {
    static func validFieldName(_ name: String) -> Bool {
        !name.isEmpty && name.unicodeScalars.allSatisfy { CharacterSet.letters.contains($0) || CharacterSet.decimalDigits.contains($0) }
    }

    static func valueNames(from text: String) -> [String] {
        text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    static func duplicate(in names: [String]) -> String? {
        var seen = Set<String>()
        for name in names {
            let key = name.lowercased()
            if !seen.insert(key).inserted { return name }
        }
        return nil
    }

    static func moved<T>(_ items: [T], from: Int, to: Int) -> [T] {
        guard items.indices.contains(from), to >= 0, to < items.count, from != to else { return items }
        var copy = items
        copy.insert(copy.remove(at: from), at: to)
        return copy
    }

    static func changesValues(old: SettingsFieldItem, type: FieldType, multiple: Bool, enumId: UUID?) -> Bool {
        old.type != type || old.enumId != (type == .enum ? enumId : nil) || (old.multiple && !multiple)
    }

    static func reference(_ task: SettingsTaskBrief, prefixes: [UUID: String]) -> String {
        if let number = task.numbers.first, let prefix = prefixes[number.seriesId] { return "\(prefix)-\(number.number)" }
        return task.title
    }

    static func canRemoveMember(_ member: SettingsMember, among members: [SettingsMember]) -> Bool {
        guard !member.isAgent else { return true }
        return members.filter { !$0.isAgent }.count > 1
    }
}
