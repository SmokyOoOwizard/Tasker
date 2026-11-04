import SwiftUI

@main
struct TaskerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    var body: some Scene {
        Window(Localizer(choice: AppLanguage(storedValue: storedLanguage)).string(.appName), id: "main") {
            MainView()
                .background(WindowFrameAutosave(name: "TaskerMainWindow"))
                .localized()
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            NavigationCommands()
        }
    }
}
