import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var connection = DaemonConnection.live()
    @State private var live: LiveWorkspace
    @State private var workspaces: WorkspaceController
    @State private var projects: ProjectSelection
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    init() {
        let live = LiveWorkspace()
        let workspaces = WorkspaceController.live()
        _live = State(initialValue: live)
        _workspaces = State(initialValue: workspaces)
        _projects = State(initialValue: ProjectSelection(tabs: workspaces.tabs, live: live))
    }

    var body: some Scene {
        Window(Localizer(choice: AppLanguage(storedValue: storedLanguage)).string(.appName), id: "main") {
            MainView()
                .background(WindowFrameAutosave(name: "TaskerMainWindow"))
                .localized()
                .environment(connection)
                .environment(live)
                .environment(workspaces)
                .environment(projects)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands()
        }
    }
}
