import SwiftUI

struct AppSettingsView: View {
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue
    @AppStorage(AppTheme.storageKey) private var storedTheme = AppTheme.system.rawValue
    @AppStorage(StartupBehavior.storageKey) private var storedStartup = StartupBehavior.restore.rawValue
    @Environment(\.localizer) private var l

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
        }
        .formStyle(.grouped)
        .font(.design(DesignTypography.baseSize))
        .navigationTitle(l.string(.sectionSettings))
        .frame(width: 520)
        .fixedSize(horizontal: false, vertical: true)
    }
}
