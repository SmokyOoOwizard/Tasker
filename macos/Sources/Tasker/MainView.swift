import SwiftUI

struct WorkspaceWindowRoot: View {
    @Binding var requested: UUID?
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(\.localizer) private var l
    @Environment(\.openWindow) private var openWindow
    @State private var id: UUID?

    private var layout: WorkspaceLayout { workspaces.layout }

    var body: some View {
        Group {
            if let id, let window = layout.window(id) {
                MainView(window: window)
            } else {
                DesignColor.bg.color
                    .frame(minWidth: 640, minHeight: 420)
            }
        }
        .background {
            if let id {
                WorkspaceWindowBinder(id: id, layout: layout, close: layout.window(id) == nil) {
                    sessions.confirmClosing(window: id, l)
                }
            }
        }
        .onAppear(perform: claim)
    }

    private func claim() {
        guard id == nil else { return }
        let claimed = layout.claimWindow(requested: requested)
        id = claimed
        if requested != claimed { requested = claimed }
        for pending in layout.takePendingWindows() {
            openWindow(value: pending)
        }
    }
}

struct MainView: View {
    let window: WorkspaceWindow
    @Environment(DaemonConnection.self) private var connection
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(\.localizer) private var l
    @Environment(\.commandRunner) private var runner

    private var layout: WorkspaceLayout { workspaces.layout }

    private var session: WorkspaceTabSession {
        window.selectedTab.map(sessions.session(for:)) ?? sessions.idle
    }

    private var selection: Binding<AppSection> {
        let session = session
        return Binding(
            get: { session.section },
            set: { session.setSection($0) }
        )
    }

    private var active: ActiveWorkspaceWindow {
        ActiveWorkspaceWindow(
            id: window.id,
            tab: window.selected,
            sidebarVisible: window.sidebarVisible,
            commands: runner.map { $0.state(window: window.id) } ?? .none,
            others: layout.windows.filter { $0.id != window.id }.map { .init(id: $0.id, title: $0.title(fallback: l.string(.appName))) }
        )
    }

    private func toggleSidebar() {
        layout.setSidebar(!window.sidebarVisible, in: window.id)
    }

    var body: some View {
        let session = session
        VStack(spacing: 0) {
            WorkspaceTabBar(window: window)
            WorkspaceFailureBanner()
            HStack(spacing: 0) {
            if window.sidebarVisible {
                SidebarView(selection: selection)
                    .transition(.move(edge: .leading))
            }
            VStack(spacing: 0) {
                TopBarView(section: selection.wrappedValue, sidebarVisible: window.sidebarVisible, toggleSidebar: toggleSidebar)
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
        .overlay {
            if let runner, runner.shortcuts.window == window.id {
                ShortcutsDialog { runner.shortcuts.hide() }
            }
        }
        .environment(\.workspaceWindow, window.id)
        .environment(session)
        .environment(session.live)
        .environment(session.selection)
        .environment(session.settings)
        .environment(session.creation)
        .ignoresSafeArea()
        .animation(.easeInOut(duration: 0.18), value: window.sidebarVisible)
        .frame(minWidth: 640, minHeight: 420)
        .font(.design(DesignTypography.baseSize))
        .foregroundStyle(DesignColor.tx.color)
        .tint(DesignColor.acc.color)
        .navigationTitle(window.title(fallback: l.string(.appName)))
        .focusedSceneValue(\.activeWorkspaceWindow, active)
        .task { connection.start() }
        .onChange(of: connection.state, initial: true) {
            workspaces.sync(with: connection.state)
            sessions.sync(with: connection.state)
        }
        .onChange(of: layout.openPaths) {
            sessions.sync(with: connection.state)
        }
    }
}
