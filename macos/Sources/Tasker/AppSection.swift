import Foundation

enum AppSection: String, CaseIterable, Identifiable, Sendable {
    case tasks
    case boards
    case settings
    case admin

    static let storageKey = "selectedSection"
    static let `default`: AppSection = .tasks

    var id: String { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .tasks: .sectionTasks
        case .boards: .sectionBoards
        case .settings: .sectionSettings
        case .admin: .sectionAdmin
        }
    }

    var icon: MockupIcon {
        switch self {
        case .tasks: .list
        case .boards: .board
        case .settings: .gear
        case .admin: .shield
        }
    }

    var shortcutKey: Character {
        switch self {
        case .tasks: "1"
        case .boards: "2"
        case .settings: "3"
        case .admin: "4"
        }
    }

    init(storedValue: String) {
        self = AppSection(rawValue: storedValue) ?? .default
    }
}
