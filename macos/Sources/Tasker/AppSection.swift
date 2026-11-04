import Foundation

enum AppSection: String, CaseIterable, Identifiable, Sendable {
    case tasks
    case boards
    case settings
    case admin

    static let storageKey = "selectedSection"
    static let `default`: AppSection = .tasks

    var id: String { rawValue }

    var title: String {
        switch self {
        case .tasks: "Задачи"
        case .boards: "Доски"
        case .settings: "Настройки"
        case .admin: "Админ-панель"
        }
    }

    var symbol: String {
        switch self {
        case .tasks: "list.bullet"
        case .boards: "rectangle.split.3x1"
        case .settings: "gearshape"
        case .admin: "checkmark.shield"
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
