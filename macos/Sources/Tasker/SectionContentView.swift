import SwiftUI

struct SectionContentView: View {
    let section: AppSection

    var body: some View {
        VStack(spacing: 8) {
            MockupIconView(icon: section.icon, size: 36)
                .foregroundStyle(DesignColor.dim.color)
            Text(section.title)
                .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
            Text(Strings.placeholderHint)
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.mut.color)
            Text(Strings.version(AppInfo.version))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
