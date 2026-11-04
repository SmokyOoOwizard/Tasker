import AppKit
import SwiftUI

enum SidebarState {
    static let storageKey = "sidebarVisible"
}

struct NavigationCommands: Commands {
    @AppStorage(AppSection.storageKey) private var storedSection = AppSection.default.rawValue
    @AppStorage(SidebarState.storageKey) private var sidebarVisible = true
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    private var l: Localizer {
        Localizer(choice: AppLanguage(storedValue: storedLanguage))
    }

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button(l.string(.menuAbout, l.string(.appName))) {
                showAbout()
            }
        }
        CommandGroup(replacing: .appSettings) {
            SettingsLink {
                Text(l.string(.menuSettings))
            }
            .keyboardShortcut(",", modifiers: .command)
        }
        CommandGroup(replacing: .sidebar) {
            Button(sidebarVisible ? l.string(.sidebarHide) : l.string(.sidebarShow)) {
                sidebarVisible.toggle()
            }
            .keyboardShortcut("0", modifiers: .command)
            Divider()
            ForEach(AppSection.allCases) { section in
                Button(l.string(section.titleKey)) {
                    storedSection = section.rawValue
                }
                .keyboardShortcut(KeyEquivalent(section.shortcutKey), modifiers: .command)
            }
        }
    }

    private func showAbout() {
        let credits = NSAttributedString(string: l.string(.appCredits))
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationName: l.string(.appName),
            .applicationVersion: AppInfo.version,
            .credits: credits
        ])
    }
}
