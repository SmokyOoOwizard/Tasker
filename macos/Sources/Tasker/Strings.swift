import Foundation

enum Strings {
    static let appName = "Tasker"
    static let searchPlaceholder = "Поиск и команды"
    static let searchShortcut = "⌘K"
    static let projectMenuTitle = "Проект"
    static let profile = "Профиль"
    static let showSidebar = "Показать боковую панель"
    static let hideSidebar = "Скрыть боковую панель"
    static let sectionsMenu = "Разделы"
    static let placeholderHint = "Раздел появится в следующих версиях"

    static func version(_ value: String) -> String {
        "Версия \(value)"
    }
}
