import SwiftUI

struct MainView: View {
    @AppStorage(AppSection.storageKey) private var storedSection = AppSection.default.rawValue
    @Environment(DaemonConnection.self) private var connection
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceController.self) private var workspaces
    @AppStorage(SidebarState.storageKey) private var sidebarVisible = true

    private var selection: Binding<AppSection> {
        Binding(
            get: { AppSection(storedValue: storedSection) },
            set: { storedSection = $0.rawValue }
        )
    }

    var body: some View {
        VStack(spacing: 0) {
            WorkspaceTabBar()
            WorkspaceFailureBanner()
            HStack(spacing: 0) {
            if sidebarVisible {
                SidebarView(selection: selection)
                    .transition(.move(edge: .leading))
            }
            VStack(spacing: 0) {
                TopBarView(section: selection.wrappedValue, sidebarVisible: sidebarVisible) {
                    sidebarVisible.toggle()
                }
                DaemonGate {
                    WorkspaceGate {
                        SectionContentView(section: selection.wrappedValue)
                    }
                }
                .overlay(alignment: .bottom) {
                    LiveToastStack()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(DesignColor.bg.color)
            }
        }
        .ignoresSafeArea()
        .animation(.easeInOut(duration: 0.18), value: sidebarVisible)
        .frame(minWidth: 640, minHeight: 420)
        .font(.design(DesignTypography.baseSize))
        .foregroundStyle(DesignColor.tx.color)
        .tint(DesignColor.acc.color)
        .task { connection.start() }
        .onChange(of: connection.state, initial: true) {
            workspaces.sync(with: connection.state)
            live.follow(connection.state, selected: workspaces.tabs.selected)
        }
        .onChange(of: workspaces.tabs.selected) {
            live.follow(connection.state, selected: workspaces.tabs.selected)
        }
    }
}
