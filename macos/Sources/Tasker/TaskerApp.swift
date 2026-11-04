import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        Window("Tasker", id: "main") {
            MainView()
                .background(WindowFrameAutosave(name: "TaskerMainWindow"))
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands()
        }
    }
}
