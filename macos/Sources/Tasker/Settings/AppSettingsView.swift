import SwiftUI
import UserNotifications

struct AppSettingsView: View {
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue
    @AppStorage(AppTheme.storageKey) private var storedTheme = AppTheme.system.rawValue
    @AppStorage(StartupBehavior.storageKey) private var storedStartup = StartupBehavior.restore.rawValue
    @AppStorage(NotificationPreferences.enabledKey) private var notificationsEnabled = true
    @Environment(\.localizer) private var l
    @Environment(\.taskNotifier) private var notifier
    @State private var authorization: UNAuthorizationStatus?

    var body: some View {
        Form {
            Picker(l.string(.appSettingsLanguage), selection: $storedLanguage) {
                ForEach(AppLanguage.allCases) { Text(l.string($0.labelKey)).tag($0.rawValue) }
            }
            Picker(l.string(.appSettingsTheme), selection: Binding(
                get: { AppTheme(storedValue: storedTheme).rawValue },
                set: { AppTheme.set(AppTheme(storedValue: $0)) }
            )) {
                ForEach(AppTheme.allCases, id: \.rawValue) { Text(l.string($0.titleKey)).tag($0.rawValue) }
            }
            Picker(l.string(.appSettingsStartup), selection: $storedStartup) {
                ForEach(StartupBehavior.allCases) { Text(l.string($0.titleKey)).tag($0.rawValue) }
            }
            .pickerStyle(.radioGroup)
            Text(l.string(.startupNote))
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.mut.color)
            Section(l.string(.notificationsHeading)) {
                Toggle(l.string(.notificationsEnabled), isOn: $notificationsEnabled)
                    .onChange(of: notificationsEnabled) {
                        guard notificationsEnabled else { return }
                        Task {
                            await notifier?.requestIfNeeded()
                            authorization = await notifier?.status()
                        }
                    }
                ForEach(NotificationKind.allCases) { kind in
                    NotificationKindToggle(kind: kind)
                        .disabled(!notificationsEnabled)
                }
                Text(l.string(.notificationsNote))
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
                if authorization == .denied {
                    Text(l.string(.notificationsDenied))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                    Button(l.string(.notificationsOpenSystem)) {
                        if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") {
                            NSWorkspace.shared.open(url)
                        }
                    }
                }
            }
        }
        .task { authorization = await notifier?.status() }
        .formStyle(.grouped)
        .font(.design(DesignTypography.baseSize))
        .navigationTitle(l.string(.sectionSettings))
        .frame(width: 520)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct NotificationKindToggle: View {
    let kind: NotificationKind
    @AppStorage private var isOn: Bool
    @Environment(\.localizer) private var l

    init(kind: NotificationKind) {
        self.kind = kind
        _isOn = AppStorage(wrappedValue: kind.defaultEnabled, kind.storageKey)
    }

    var body: some View {
        Toggle(l.string(kind.titleKey), isOn: $isOn)
    }
}
