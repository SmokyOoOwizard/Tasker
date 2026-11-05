import AppKit
import Foundation

struct CommandChoice: Equatable, Sendable {
    var addToPath: Bool
    var connectCompletion: Bool
}

struct CommandOffer: Equatable, Sendable {
    var link: String
    var replaces: String?
    var pathFile: String?
    var pathDirectory: String
    var completionFile: String?
}

@MainActor
protocol IntegrationPrompting: AnyObject {
    func confirmCommand(_ offer: CommandOffer) -> CommandChoice?
    func confirmRemoval() -> Bool
    func inform(title: L10nKey, text: String)
    func quit()
}

@MainActor
final class AppIntegrationController {
    static let declinedKey = "commandPromptDeclined"

    let location: InstallLocation
    let integration: AppIntegration?
    private let prompter: any IntegrationPrompting
    private let defaults: UserDefaults
    private let localizer: () -> Localizer

    private(set) var report = IntegrationReport()
    var stopConnection: () -> Void = {}

    init(
        location: InstallLocation,
        integration: AppIntegration?,
        prompter: any IntegrationPrompting,
        defaults: UserDefaults = .standard,
        localizer: @escaping () -> Localizer
    ) {
        self.location = location
        self.integration = integration
        self.prompter = prompter
        self.defaults = defaults
        self.localizer = localizer
    }

    var allowsAutomaticDaemonUpdate: Bool { location == .installed && report.migrationError == nil }

    func prepare() async {
        await reconcile()
        await presentNotices()
    }

    func reconcile() async {
        guard location == .installed, let integration else { return }
        report = await integration.reconcile()
    }

    func presentNotices() async {
        guard location == .installed, integration != nil else {
            if location == .temporary {
                prompter.inform(title: .installTemporaryTitle, text: localizer().string(.installTemporaryBody))
            }
            return
        }
        let l = localizer()
        if report.migrated {
            prompter.inform(title: .installMigratedTitle, text: l.string(.installMigratedBody))
        }
        if let error = report.migrationError {
            prompter.inform(title: .installMigrationFailedTitle, text: l.string(.installMigrationFailedBody, error))
        }
        if report.needsCommandPrompt, !defaults.bool(forKey: Self.declinedKey) {
            await offerCommand()
        }
    }

    func installCommandFromMenu() async {
        guard location == .installed, integration != nil else { return informUnavailable() }
        await offerCommand()
    }

    func removeFromMenu() async {
        guard location == .installed, let integration else { return informUnavailable() }
        guard prompter.confirmRemoval() else { return }
        stopConnection()
        do {
            try await integration.removeAll()
            defaults.removeObject(forKey: Self.declinedKey)
            prompter.quit()
        } catch {
            prompter.inform(title: .installFailedTitle, text: Self.describe(error))
        }
    }

    private func informUnavailable() {
        let l = localizer()
        if location == .temporary {
            prompter.inform(title: .installTemporaryTitle, text: l.string(.installTemporaryBody))
        } else {
            prompter.inform(title: .installUnavailableTitle, text: l.string(.installUnavailableBody))
        }
    }

    private func offerCommand() async {
        guard let integration else { return }
        let onPath = await integration.isCommandOnPath()
        let profile = integration.profile
        var replaces: String?
        if case .foreign(let target) = CommandLinkState.read(integration.paths) { replaces = target }
        let home = integration.paths.home
        let offer = CommandOffer(
            link: Self.shortPath(integration.paths.commandLink, home: home),
            replaces: replaces,
            pathFile: onPath == true ? nil : Self.shortPath(profile.pathFile, home: home),
            pathDirectory: Self.shortPath(integration.paths.commandDirectory, home: home),
            completionFile: integration.isCompletionConnected ? nil : profile.completionFile.map { Self.shortPath($0, home: home) }
        )
        guard let choice = prompter.confirmCommand(offer) else {
            defaults.set(true, forKey: Self.declinedKey)
            return
        }
        let l = localizer()
        do {
            try integration.installLink()
            if choice.addToPath { try integration.addToPath() }
            if choice.connectCompletion {
                try await integration.connectCompletion()
            } else {
                await integration.refreshCompletions()
            }
            let reachable = onPath == true || choice.addToPath
            prompter.inform(
                title: .installDoneTitle,
                text: reachable ? l.string(.installDoneBody) : l.string(.installDoneNoPath, offer.pathDirectory)
            )
        } catch {
            prompter.inform(title: .installFailedTitle, text: Self.describe(error))
        }
    }

    private static func describe(_ error: any Error) -> String {
        (error as? DaemonServiceError)?.message ?? error.localizedDescription
    }

    private static func shortPath(_ url: URL, home: URL) -> String {
        url.path.hasPrefix(home.path + "/") ? "~" + url.path.dropFirst(home.path.count) : url.path
    }

    static func live() -> AppIntegrationController {
        let environment = ProcessInfo.processInfo.environment
        let location = InstallLocation.current
        let localizer = { Localizer(choice: AppLanguage(storedValue: UserDefaults.standard.string(forKey: AppLanguage.storageKey) ?? "")) }
        let prompter = AlertIntegrationPrompter(localizer: localizer)
        guard location == .installed else {
            return AppIntegrationController(location: location, integration: nil, prompter: prompter, localizer: localizer)
        }
        let home = DaemonPaths.installHome(environment: environment)
        let daemonPaths = DaemonPaths(environment: environment, home: home)
        let legacyLabel = LegacyInstallation.label(environment: environment)
        let integration = AppIntegration(
            paths: InstallPaths(bundle: Bundle.main.bundleURL, home: home),
            daemonPaths: daemonPaths,
            legacyLabel: legacyLabel,
            version: AppInfo.version,
            agent: DaemonLaunchAgent.live(paths: daemonPaths, environment: environment),
            transport: URLSessionDaemonTransport(),
            runner: SystemCommandRunner(),
            shellPath: environment["SHELL"]
        )
        return AppIntegrationController(location: location, integration: integration, prompter: prompter, localizer: localizer)
    }
}

@MainActor
final class AlertIntegrationPrompter: IntegrationPrompting {
    private let localizer: () -> Localizer

    init(localizer: @escaping () -> Localizer) {
        self.localizer = localizer
    }

    func confirmCommand(_ offer: CommandOffer) -> CommandChoice? {
        let l = localizer()
        let alert = NSAlert()
        alert.messageText = l.string(.installPromptTitle)
        var text = l.string(.installPromptBody, offer.link)
        if let replaces = offer.replaces {
            text += "\n\n" + l.string(.installPromptReplace, offer.link, replaces)
        }
        alert.informativeText = text
        alert.addButton(withTitle: l.string(.installPromptInstall))
        alert.addButton(withTitle: l.string(.installPromptLater))

        var boxes: [NSButton] = []
        var pathBox: NSButton?
        var completionBox: NSButton?
        if let file = offer.pathFile {
            let box = Self.checkbox(l.string(.installPromptPath, offer.pathDirectory, file))
            pathBox = box
            boxes.append(box)
        }
        if let file = offer.completionFile {
            let box = Self.checkbox(l.string(.installPromptCompletion, file))
            completionBox = box
            boxes.append(box)
        }
        if !boxes.isEmpty {
            let stack = NSStackView(views: boxes)
            stack.orientation = .vertical
            stack.alignment = .leading
            stack.frame = NSRect(origin: .zero, size: stack.fittingSize)
            alert.accessoryView = stack
        }
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return CommandChoice(addToPath: pathBox?.state == .on, connectCompletion: completionBox?.state == .on)
    }

    private static func checkbox(_ title: String) -> NSButton {
        let box = NSButton(checkboxWithTitle: title, target: nil, action: nil)
        box.state = .on
        box.cell?.wraps = true
        box.widthAnchor.constraint(lessThanOrEqualToConstant: 300).isActive = true
        return box
    }

    func confirmRemoval() -> Bool {
        let l = localizer()
        let alert = NSAlert()
        alert.messageText = l.string(.installRemoveTitle)
        alert.informativeText = l.string(.installRemoveBody)
        alert.alertStyle = .warning
        alert.addButton(withTitle: l.string(.installRemoveConfirm))
        alert.addButton(withTitle: l.string(.installPromptLater))
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    func inform(title: L10nKey, text: String) {
        let alert = NSAlert()
        alert.messageText = localizer().string(title)
        alert.informativeText = text
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    func quit() {
        NSApp.terminate(nil)
    }
}

struct DirectDaemonStart: DaemonService {
    func start(program: String) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: program)
        process.arguments = ["--detached"]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            throw DaemonServiceError(message: error.localizedDescription)
        }
    }
}
