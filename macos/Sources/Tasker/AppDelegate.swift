import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    var layout: WorkspaceLayout?
    var releaseLocks: (() -> Task<Void, Never>?)?
    var openLinks: (([URL]) -> Void)? {
        didSet {
            guard let openLinks, !pendingLinks.isEmpty else { return }
            let urls = pendingLinks
            pendingLinks = []
            openLinks(urls)
        }
    }
    private var pendingLinks: [URL] = []
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
        LatinKeys.install()
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.initial, .new]) { _, _ in
            MainActor.assumeIsolated { AppDelegate.updateIcon() }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            if !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
                _ = NSApp.delegate?.applicationOpenUntitledFile?(NSApp)
            }
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        let links = urls.filter { $0.scheme?.lowercased() == TaskerLink.scheme }
        guard !links.isEmpty else { return }
        if let openLinks {
            openLinks(links)
        } else {
            pendingLinks += links
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        layout?.prepareToTerminate()
        guard let release = releaseLocks?() else { return .terminateNow }
        Task { @MainActor in
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await release.value }
                group.addTask { try? await Task.sleep(for: .seconds(1.5)) }
                await group.next()
                group.cancelAll()
            }
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    private static func updateIcon() {
        let isDark = NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        NSApp.applicationIconImage = NSImage(named: isDark ? "IconDark" : "IconLight")
    }
}
