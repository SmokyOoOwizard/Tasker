import SwiftUI

struct SettingsView: View {
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue
    @Environment(\.localizer) private var l

    private var selection: Binding<AppLanguage> {
        Binding(
            get: { AppLanguage(storedValue: storedLanguage) },
            set: { storedLanguage = $0.rawValue }
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap8) {
            Text(l.string(.settingsLanguageTitle))
                .font(.designEmphasis(DesignTypography.baseSize))
            Text(l.string(.settingsLanguageHint))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.mut.color)
                .fixedSize(horizontal: false, vertical: true)
            Picker(selection: selection) {
                ForEach(AppLanguage.allCases) { option in
                    Text(l.string(option.labelKey)).tag(option)
                }
            } label: {
                EmptyView()
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            .padding(.vertical, DesignSpacing.gap6)
            Text(l.string(.settingsLanguageRestart))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .fixedSize(horizontal: false, vertical: true)
            DesignDivider().padding(.vertical, DesignSpacing.gap6)
            Text(l.string(.settingsPreviewTitle))
                .font(.designEmphasis(DesignTypography.baseSize))
            Text(l.string(.settingsPreviewSample, l.taskCount(5), l.date(Self.sampleDate), l.number(1234.5)))
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.mut.color)
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: 420, alignment: .leading)
        .background(DesignColor.bg.color)
        .foregroundStyle(DesignColor.tx.color)
        .tint(DesignColor.acc.color)
        .navigationTitle(l.string(.sectionSettings))
    }

    static let sampleDate = Date(timeIntervalSince1970: 1_793_750_400)
}
