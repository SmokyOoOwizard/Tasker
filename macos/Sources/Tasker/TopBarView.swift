import SwiftUI

struct TopBarView: View {
    let section: AppSection
    @Environment(\.localizer) private var l
    let sidebarVisible: Bool
    let toggleSidebar: () -> Void

    var body: some View {
        HStack(spacing: DesignSpacing.gap10) {
            if !sidebarVisible {
                Button(action: toggleSidebar) {
                    Image(systemName: "sidebar.left")
                }
                .buttonStyle(.plain)
                .foregroundStyle(DesignColor.mut.color)
                .help(l.string(.sidebarShow))
            }
            Text(l.string(.appName))
                .font(.designEmphasis(DesignTypography.baseSize))
            Text(l.string(.breadcrumbSeparator))
                .foregroundStyle(DesignColor.dim.color)
            Text(l.string(section.titleKey))
                .font(.designEmphasis(DesignTypography.baseSize))
            Spacer(minLength: 0)
        }
        .padding(.leading, sidebarVisible ? DesignSpacing.pagePadX : 80)
        .padding(.trailing, DesignSpacing.pagePadX)
        .frame(height: DesignLayout.topBarHeight)
        .overlay(alignment: .bottom) {
            DesignDivider()
        }
    }
}
