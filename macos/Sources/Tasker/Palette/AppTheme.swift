import AppKit

enum AppTheme: String, CaseIterable, Sendable {
    case system
    case light
    case dark

    static let storageKey = "appTheme"

    init(storedValue: String) {
        self = AppTheme(rawValue: storedValue) ?? .system
    }

    var appearance: NSAppearance? {
        switch self {
        case .system: nil
        case .light: NSAppearance(named: .aqua)
        case .dark: NSAppearance(named: .darkAqua)
        }
    }

    @MainActor
    static func current(defaults: UserDefaults = .standard) -> AppTheme {
        AppTheme(storedValue: defaults.string(forKey: storageKey) ?? "")
    }

    @MainActor
    static func set(_ theme: AppTheme, defaults: UserDefaults = .standard) {
        defaults.set(theme.rawValue, forKey: storageKey)
        theme.apply()
    }

    @MainActor
    func apply() {
        NSApp?.appearance = appearance
    }

    @MainActor
    static var effectiveIsDark: Bool {
        (NSApp?.effectiveAppearance ?? NSAppearance.currentDrawing()).bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    }
}
