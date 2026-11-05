import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    var layout: WorkspaceLayout?
    private var appearanceObservation: NSKeyValueObservation?

    func applicationWillFinishLaunching(_ notification: Notification) {
        NSWindow.allowsAutomaticWindowTabbing = false
        guard let bundleID = Bundle.main.bundleIdentifier else { return }
        let current = ProcessInfo.processInfo.processIdentifier
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
            .filter { $0.processIdentifier != current }
        if let existing = others.first {
            existing.activate()
            exit(0)
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppTheme.current().apply()
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.initial, .new]) { _, _ in
            MainActor.assumeIsolated { AppDelegate.updateIcon() }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            if !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
                _ = NSApp.delegate?.applicationOpenUntitledFile?(NSApp)
            }
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        layout?.prepareToTerminate()
        return .terminateNow
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    private static func updateIcon() {
        let isDark = NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        NSApp.applicationIconImage = NSImage(named: isDark ? "IconDark" : "IconLight")
    }
}
