import SwiftUI

struct TopBarView: View {
    let section: AppSection
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
                .help(Strings.showSidebar)
            }
            Text(Strings.appName)
                .font(.designEmphasis(DesignTypography.baseSize))
            Text("/")
                .foregroundStyle(DesignColor.dim.color)
            Text(section.title)
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
