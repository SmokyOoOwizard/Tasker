import SwiftUI

enum SidebarState {
    static let storageKey = "sidebarVisible"
}

struct NavigationCommands: Commands {
    @AppStorage(AppSection.storageKey) private var storedSection = AppSection.default.rawValue
    @AppStorage(SidebarState.storageKey) private var sidebarVisible = true

    var body: some Commands {
        CommandGroup(replacing: .sidebar) {
            Button(sidebarVisible ? Strings.hideSidebar : Strings.showSidebar) {
                sidebarVisible.toggle()
            }
            .keyboardShortcut("0", modifiers: .command)
            Divider()
            ForEach(AppSection.allCases) { section in
                Button(section.title) {
                    storedSection = section.rawValue
                }
                .keyboardShortcut(KeyEquivalent(section.shortcutKey), modifiers: .command)
            }
        }
    }
}
