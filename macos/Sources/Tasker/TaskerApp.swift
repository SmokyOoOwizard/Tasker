import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var connection: DaemonConnection
    @State private var workspaces: WorkspaceController
    @State private var sessions: WorkspaceSessions
    @State private var palette: CommandPalette
    @State private var runner: CommandRunner
    @State private var opener: LinkOpener
    @State private var notifier: TaskNotifier
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    init() {
        UserDefaults.standard.register(defaults: ["NSQuitAlwaysKeepsWindows": false])
        let workspaces = WorkspaceController.live()
        _workspaces = State(initialValue: workspaces)
        let sessions = WorkspaceSessions(layout: workspaces.layout)
        _sessions = State(initialValue: sessions)
        let integration = AppIntegrationController.live()
        let connection = DaemonConnection.live(integration: integration)
        integration.stopConnection = { [weak connection] in connection?.stop() }
        _connection = State(initialValue: connection)
        let runner = CommandRunner(layout: workspaces.layout, sessions: sessions, workspaces: workspaces)
        _runner = State(initialValue: runner)
        let palette = CommandPalette(layout: workspaces.layout, sessions: sessions, workspaces: workspaces, connection: connection, runner: runner)
        runner.palette = palette
        runner.integration = integration
        _palette = State(initialValue: palette)
        let opener = LinkOpener(layout: workspaces.layout, sessions: sessions, workspaces: workspaces, connection: connection, runner: runner)
        _opener = State(initialValue: opener)
        let notifier = TaskNotifier.live()
        notifier.onOpen = { [weak opener] link in opener?.open(link) }
        sessions.onUpdate = { [weak notifier] path, update in
            guard let notifier else { return }
            Task { await notifier.handle(update, path: path) }
        }
        _notifier = State(initialValue: notifier)
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
                .environment(\.taskNotifier, notifier)
                .onAppear {
                    appDelegate.openLinks = { [opener] urls in opener.open(urls) }
                    Task { await notifier.prepare() }
                    appDelegate.layout = workspaces.layout
                    LatinKeys.dispatch = { [runner] command, window in runner.perform(command, window: window) }
                    appDelegate.releaseLocks = { [sessions] in sessions.releaseEditorLocks() }
                }
        }
        .handlesExternalEvents(matching: [])
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands(runner: runner)
        }
        Settings {
            AppSettingsView()
                .localized()
                .environment(\.taskNotifier, notifier)
        }
    }
}
