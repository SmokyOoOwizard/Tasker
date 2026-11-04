import SwiftUI

struct TopBarView: View {
    let section: AppSection
    let sidebarVisible: Bool
    let toggleSidebar: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            if !sidebarVisible {
                Button(action: toggleSidebar) {
                    Image(systemName: "sidebar.left")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help(Strings.showSidebar)
            }
            Text(Strings.appName)
                .fontWeight(.medium)
            Text("/")
                .foregroundStyle(.tertiary)
            Text(section.title)
                .fontWeight(.medium)
            Spacer(minLength: 0)
        }
        .padding(.leading, sidebarVisible ? 20 : 80)
        .padding(.trailing, 20)
        .frame(height: 44)
        .overlay(alignment: .bottom) {
            Divider()
        }
    }
}
