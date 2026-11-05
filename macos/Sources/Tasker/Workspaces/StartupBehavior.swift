import Foundation

enum StartupBehavior: String, CaseIterable, Identifiable, Sendable {
    case restore
    case empty
    case lastFolder

    static let storageKey = "startupBehavior"

    var id: String { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .restore: .startupRestore
        case .empty: .startupEmpty
        case .lastFolder: .startupLastFolder
        }
    }

    init(storedValue: String) {
        self = StartupBehavior(rawValue: storedValue) ?? .restore
    }

    static func current(defaults: UserDefaults = .standard) -> StartupBehavior {
        StartupBehavior(storedValue: defaults.string(forKey: storageKey) ?? "")
    }
}
