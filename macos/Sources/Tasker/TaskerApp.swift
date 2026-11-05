import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var connection: DaemonConnection
    @State private var workspaces: WorkspaceController
    @State private var sessions: WorkspaceSessions
    @State private var palette: CommandPalette
    @State private var runner: CommandRunner
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    init() {
        UserDefaults.standard.register(defaults: ["NSQuitAlwaysKeepsWindows": false])
        let workspaces = WorkspaceController.live()
        _workspaces = State(initialValue: workspaces)
        let sessions = WorkspaceSessions(layout: workspaces.layout)
        _sessions = State(initialValue: sessions)
        let connection = DaemonConnection.live()
        _connection = State(initialValue: connection)
        let runner = CommandRunner(layout: workspaces.layout, sessions: sessions, workspaces: workspaces)
        _runner = State(initialValue: runner)
        let palette = CommandPalette(layout: workspaces.layout, sessions: sessions, workspaces: workspaces, connection: connection, runner: runner)
        runner.palette = palette
        _palette = State(initialValue: palette)
        AppTheme.current().apply()
    }

    var body: some Scene {
        WindowGroup(Localizer(choice: AppLanguage(storedValue: storedLanguage)).string(.appName), id: "main", for: UUID.self) { $window in
            WorkspaceWindowRoot(requested: $window)
                .localized()
                .environment(connection)
                .environment(workspaces)
                .environment(sessions)
                .environment(\.commandPalette, palette)
                .environment(\.commandRunner, runner)
                .onAppear {
                    appDelegate.layout = workspaces.layout
                    appDelegate.releaseLocks = { [sessions] in sessions.releaseEditorLocks() }
                }
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands(runner: runner)
        }
        Window(Localizer(choice: AppLanguage(storedValue: storedLanguage)).string(.shortcutsTitle), id: ShortcutsWindow.id) {
            ShortcutsWindow()
                .localized()
        }
        .windowResizability(.contentSize)
        Settings {
            AppSettingsView()
                .localized()
        }
    }
}
