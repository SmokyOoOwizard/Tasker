import SwiftUI

struct SectionContentView: View {
    let section: AppSection

    var body: some View {
        VStack(spacing: 8) {
            MockupIconView(icon: section.icon, size: 36)
                .foregroundStyle(.tertiary)
            Text(section.title)
                .font(.system(size: 22, weight: .semibold))
            Text(Strings.placeholderHint)
                .font(.callout)
                .foregroundStyle(.secondary)
            Text(Strings.version(AppInfo.version))
                .font(.caption)
                .foregroundStyle(.tertiary)
                .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
