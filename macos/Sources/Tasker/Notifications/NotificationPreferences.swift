import Foundation

enum NotificationKind: String, CaseIterable, Identifiable, Sendable {
    case created
    case status
    case changed
    case deleted

    var id: String { rawValue }

    var storageKey: String { "notifications.kind." + rawValue }

    var defaultEnabled: Bool {
        switch self {
        case .created, .status: true
        case .changed, .deleted: false
        }
    }

    var titleKey: L10nKey {
        switch self {
        case .created: .notificationsCreated
        case .status: .notificationsStatus
        case .changed: .notificationsChanged
        case .deleted: .notificationsDeleted
        }
    }
}

struct NotificationPreferences {
    static let enabledKey = "notifications.enabled"

    let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var enabled: Bool {
        defaults.object(forKey: Self.enabledKey) as? Bool ?? true
    }

    func allows(_ kind: NotificationKind) -> Bool {
        enabled && (defaults.object(forKey: kind.storageKey) as? Bool ?? kind.defaultEnabled)
    }
}
