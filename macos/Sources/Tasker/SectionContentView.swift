import SwiftUI

struct SectionContentView: View {
    let section: AppSection
    @Environment(\.localizer) private var l

    var body: some View {
        if section == .tasks {
            LiveOverviewView()
        } else {
            placeholder
        }
    }

    private var placeholder: some View {
        VStack(spacing: 8) {
            MockupIconView(icon: section.icon, size: 36)
                .foregroundStyle(DesignColor.dim.color)
            Text(l.string(section.titleKey))
                .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
            Text(l.string(.placeholderHint))
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.mut.color)
            Text(l.string(.placeholderVersion, AppInfo.version))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
