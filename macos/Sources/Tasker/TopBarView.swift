import SwiftUI

struct TopBarView: View {
    let section: AppSection
    @Environment(WorkspaceTabSession.self) private var session
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
            if section == .tasks, session.seriesPage != nil {
                Button(l.string(.taskListTitle)) { session.closeSeries() }
                    .buttonStyle(.plain)
                    .font(.design(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.mut.color)
                Text(l.string(.breadcrumbSeparator))
                    .foregroundStyle(DesignColor.dim.color)
                Text(session.seriesPrefix ?? "")
                    .font(.design(DesignTypography.baseSize, weight: DesignTypography.semiboldWeight, mono: true))
            } else {
                Text(l.string(section == .tasks ? .taskListTitle : section.titleKey))
                    .font(.designEmphasis(DesignTypography.baseSize))
            }
            Spacer(minLength: 0)
            if section == .tasks {
                TaskListTopActions()
            }
            LiveStatusLabel()
        }
        .padding(.leading, DesignSpacing.pagePadX)
        .padding(.trailing, DesignSpacing.pagePadX)
        .frame(height: DesignLayout.topBarHeight)
        .overlay(alignment: .bottom) {
            DesignDivider()
        }
    }
}
