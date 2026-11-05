import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var connection = DaemonConnection.live()
    @State private var workspaces: WorkspaceController
    @State private var sessions: WorkspaceSessions
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    init() {
        UserDefaults.standard.register(defaults: ["NSQuitAlwaysKeepsWindows": false])
        let workspaces = WorkspaceController.live()
        _workspaces = State(initialValue: workspaces)
        _sessions = State(initialValue: WorkspaceSessions(layout: workspaces.layout))
    }

    var body: some Scene {
        WindowGroup(Localizer(choice: AppLanguage(storedValue: storedLanguage)).string(.appName), id: "main", for: UUID.self) { $window in
            WorkspaceWindowRoot(requested: $window)
                .localized()
                .environment(connection)
                .environment(workspaces)
                .environment(sessions)
                .onAppear { appDelegate.layout = workspaces.layout }
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands(layout: workspaces.layout, sessions: sessions)
        }
    }
}
