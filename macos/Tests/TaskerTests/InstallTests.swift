import Foundation
import Testing
@testable import Tasker

private final class FakeLaunchd: CommandRunning, DaemonTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [[String]] = []
    private var descriptor: Int32 = -1
    let paths: DaemonPaths
    var bootstrapFails: (String) -> Bool = { _ in false }
    var answers = true
    var stopped = false

    init(paths: DaemonPaths) {
        self.paths = paths
        try? FileManager.default.createDirectory(at: paths.daemonDirectory, withIntermediateDirectories: true)
    }

    var calls: [[String]] { lock.withLock { recorded } }

    func holdLock() {
        descriptor = open(paths.lockFile.path, O_RDWR | O_CREAT, 0o600)
        flock(descriptor, LOCK_EX | LOCK_NB)
        try? JSONEncoder().encode(DaemonInfo(pid: 1, port: 1, token: "t")).write(to: paths.infoFile)
    }

    func releaseLock() {
        guard descriptor >= 0 else { return }
        close(descriptor)
        descriptor = -1
    }

    func run(_ executable: String, _ arguments: [String]) async -> CommandResult {
        lock.withLock { recorded.append([executable] + arguments) }
        if executable.hasSuffix("/tasker") {
            return CommandResult(exitCode: 0, output: "script of \(arguments.last ?? "")\n")
        }
        switch arguments.first {
        case "bootout":
            releaseLock()
            return CommandResult(exitCode: 0, output: "")
        case "bootstrap":
            let plist = arguments.last ?? ""
            if bootstrapFails(plist) { return CommandResult(exitCode: 5, output: "bootstrap failed") }
            releaseLock()
            holdLock()
            return CommandResult(exitCode: 0, output: "")
        case "print":
            return CommandResult(exitCode: 113, output: "")
        default:
            return CommandResult(exitCode: 0, output: "")
        }
    }

    func status(of info: DaemonInfo) async throws -> DaemonStatus {
        guard answers else { throw URLError(.cannotConnectToHost) }
        return DaemonStatus(pid: 1, port: 1, version: "0.1.0", build: "b", workspaces: [])
    }

    func stop(info: DaemonInfo) async throws {
        stopped = true
        releaseLock()
    }
}

private final class FakePrompter: IntegrationPrompting {
    var choice: CommandChoice?
    var removal = true
    var offers: [CommandOffer] = []
    var informed: [L10nKey] = []
    var quitCalls = 0

    func confirmCommand(_ offer: CommandOffer) -> CommandChoice? {
        offers.append(offer)
        return choice
    }

    func confirmRemoval() -> Bool { removal }
    func inform(title: L10nKey, text: String) { informed.append(title) }
    func quit() { quitCalls += 1 }
}

private struct Stand {
    let root: URL
    let home: URL
    let bundle: URL
    let paths: InstallPaths
    let daemonPaths: DaemonPaths
    let launchd: FakeLaunchd
    let integration: AppIntegration
    let fileManager = FileManager.default

    init(bundleName: String = "Applications/Tasker.app", shell: String? = "/bin/zsh") throws {
        root = fileManager.temporaryDirectory.appending(path: "tasker-install-\(UUID().uuidString)")
        home = root.appending(path: "home")
        bundle = home.appending(path: bundleName)
        paths = InstallPaths(bundle: bundle, home: home)
        try fileManager.createDirectory(at: bundle.appending(path: "Contents/Helpers"), withIntermediateDirectories: true)
        try Data().write(to: paths.console)
        try Data().write(to: paths.daemon)
        daemonPaths = DaemonPaths(
            dataDirectory: home.appending(path: "data"),
            launchAgentsDirectory: home.appending(path: "Library/LaunchAgents"),
            label: "com.technotronika.tasker.test")
        try fileManager.createDirectory(at: daemonPaths.launchAgentsDirectory, withIntermediateDirectories: true)
        launchd = FakeLaunchd(paths: daemonPaths)
        var agent = DaemonLaunchAgent(paths: daemonPaths, runner: launchd, userID: 501)
        agent.conflictingPlist = daemonPaths.launchAgentsDirectory.appending(path: "com.legacy.plist")
        agent.bootstrapAttempts = 1
        var timings = DaemonTimings()
        timings.startTimeout = .milliseconds(50)
        timings.pollWhileStarting = .milliseconds(1)
        integration = AppIntegration(
            paths: paths, daemonPaths: daemonPaths, legacyLabel: "com.legacy", version: "0.2.0",
            agent: agent, transport: launchd, runner: launchd, shellPath: shell, timings: timings)
    }

    var legacyApp: URL { home.appending(path: ".local/share/tasker/app") }
    var legacyPlist: URL { daemonPaths.launchAgentsDirectory.appending(path: "com.legacy.plist") }

    func makeLegacy(running: Bool = true) throws {
        try fileManager.createDirectory(at: legacyApp, withIntermediateDirectories: true)
        try Data("old".utf8).write(to: legacyApp.appending(path: "tasker"))
        try Data("old".utf8).write(to: legacyApp.appending(path: "tasker-mcpd"))
        try fileManager.createDirectory(at: paths.commandDirectory, withIntermediateDirectories: true)
        try fileManager.createSymbolicLink(at: paths.commandLink, withDestinationURL: legacyApp.appending(path: "tasker"))
        let plist: [String: Any] = ["Label": "com.legacy", "ProgramArguments": [legacyApp.appending(path: "tasker-mcpd").path, "--detached"]]
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: legacyPlist)
        if running { launchd.holdLock() }
    }

    func link(_ url: URL) -> String? { try? fileManager.destinationOfSymbolicLink(atPath: url.path) }
}

struct InstallLocationTests {
    @Test func stableAppIsInstalled() {
        #expect(InstallLocation.classify(bundle: "/Applications/Tasker.app", environment: [:], isDebugBuild: false) == .installed)
        #expect(InstallLocation.classify(bundle: "/Users/u/Tools/Tasker.app", environment: [:], isDebugBuild: false) == .installed)
    }

    @Test func diskImageAndTranslocationAreTemporary() {
        #expect(InstallLocation.classify(bundle: "/Volumes/Tasker/Tasker.app", environment: [:], isDebugBuild: false) == .temporary)
        #expect(InstallLocation.classify(bundle: "/private/var/folders/x/AppTranslocation/y/d/Tasker.app", environment: [:], isDebugBuild: false) == .temporary)
    }

    @Test func developmentBuildsNeverTouchTheSystem() {
        #expect(InstallLocation.classify(bundle: "/Applications/Tasker.app", environment: [:], isDebugBuild: true) == .development)
        #expect(InstallLocation.classify(bundle: "/r/macos/build/Build/Products/Release/Tasker.app", environment: [:], isDebugBuild: false) == .development)
        #expect(InstallLocation.classify(bundle: "/Applications/Tasker.app", environment: ["TASKER_HOME": "/x"], isDebugBuild: false) == .development)
    }

    @Test func standRootCoversOnlyItsOwnBundle() {
        let environment = ["TASKER_INSTALL_ROOT": "/stand", "TASKER_HOME": "/stand/data"]
        #expect(InstallLocation.classify(bundle: "/stand/Applications/Tasker.app", environment: environment, isDebugBuild: true) == .installed)
        #expect(InstallLocation.classify(bundle: "/Applications/Tasker.app", environment: environment, isDebugBuild: false) == .development)
    }

    @Test func standNeverAddressesTheRealLegacyService() {
        #expect(LegacyInstallation.label(environment: [:]) == "com.tasker.mcp")
        #expect(LegacyInstallation.label(environment: ["TASKER_INSTALL_ROOT": "/stand"]) == nil)
        #expect(LegacyInstallation.label(environment: ["TASKER_INSTALL_ROOT": "/stand", "TASKER_LEGACY_LABEL": "com.tasker.mcp"]) == nil)
        #expect(LegacyInstallation.label(environment: ["TASKER_INSTALL_ROOT": "/stand", "TASKER_LEGACY_LABEL": "com.stand"]) == "com.stand")
    }

    @Test func installRootReplacesHomeForServicePaths() {
        let paths = DaemonPaths(
            environment: ["TASKER_INSTALL_ROOT": "/stand", "TASKER_HOME": "/stand/data"],
            home: URL(fileURLWithPath: "/Users/u"))
        #expect(paths.plistFile.path.hasPrefix("/stand/Library/LaunchAgents/"))
    }
}

@MainActor
struct LegacyMigrationTests {
    @Test func replacesServiceLinkAndProgramsWithoutTouchingData() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        let settings = stand.daemonPaths.dataDirectory.appending(path: "settings.json")
        try Data("{}".utf8).write(to: settings)

        let report = await stand.integration.reconcile()

        #expect(report.migrated)
        #expect(report.migrationError == nil)
        #expect(!report.needsCommandPrompt)
        #expect(!stand.fileManager.fileExists(atPath: stand.legacyPlist.path))
        #expect(stand.fileManager.fileExists(atPath: stand.daemonPaths.plistFile.path))
        #expect(stand.link(stand.paths.commandLink) == stand.paths.console.path)
        #expect(stand.link(stand.legacyApp.appending(path: "tasker")) == stand.paths.console.path)
        #expect(stand.link(stand.legacyApp.appending(path: "tasker-mcpd")) == stand.paths.daemon.path)
        #expect(stand.fileManager.fileExists(atPath: settings.path))
        let calls = stand.launchd.calls.map { $0.joined(separator: " ") }
        #expect(calls.contains("/bin/launchctl bootout gui/501/com.legacy"))
        #expect(calls.contains { $0.hasPrefix("/bin/launchctl bootstrap gui/501") && $0.hasSuffix("com.technotronika.tasker.test.plist") })
    }

    @Test func secondLaunchFindsNothingToMigrateAndDropsTheBackup() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        _ = await stand.integration.reconcile()
        #expect(stand.fileManager.fileExists(atPath: stand.home.appending(path: ".local/share/tasker/app.previous").path))

        let report = await stand.integration.reconcile()

        #expect(!report.migrated)
        #expect(!stand.fileManager.fileExists(atPath: stand.home.appending(path: ".local/share/tasker/app.previous").path))
    }

    @Test func failedStartRollsBackToTheOldService() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        stand.launchd.bootstrapFails = { $0.hasSuffix("com.technotronika.tasker.test.plist") }

        let report = await stand.integration.reconcile()

        #expect(report.migrationError == "bootstrap failed")
        #expect(stand.fileManager.fileExists(atPath: stand.legacyPlist.path))
        #expect(!stand.fileManager.fileExists(atPath: stand.daemonPaths.plistFile.path))
        #expect(stand.link(stand.paths.commandLink) == stand.legacyApp.appending(path: "tasker").path)
        let last = stand.launchd.calls.last { $0.contains("bootstrap") }
        #expect(last?.last == stand.legacyPlist.path)
    }

    @Test func foreignCommandAtTheLinkStaysUntouched() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        try stand.fileManager.removeItem(at: stand.paths.commandLink)
        try stand.fileManager.createSymbolicLink(at: stand.paths.commandLink, withDestinationURL: URL(fileURLWithPath: "/opt/other/tasker"))

        let report = await stand.integration.reconcile()

        #expect(report.migrated)
        #expect(stand.link(stand.paths.commandLink) == "/opt/other/tasker")
    }

    @Test func serviceRefusesToStartBesideTheOldOne() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        await #expect(throws: DaemonServiceError.self) {
            try await stand.integration.agent.start(program: stand.paths.daemon.path)
        }
    }

    @Test func runningDaemonWithoutOldServiceIsStoppedToo() async throws {
        let stand = try Stand()
        try stand.makeLegacy()
        try stand.fileManager.removeItem(at: stand.legacyPlist)

        let report = await stand.integration.reconcile()

        #expect(report.migrated)
        #expect(stand.launchd.stopped)
    }
}

private final class UnloadingLaunchd: CommandRunning, @unchecked Sendable {
    private let lock = NSLock()
    private var prints = 0
    private var bootstraps = 0
    let stillLoaded: Int
    let failingBootstraps: Int

    init(stillLoaded: Int, failingBootstraps: Int) {
        self.stillLoaded = stillLoaded
        self.failingBootstraps = failingBootstraps
    }

    var bootstrapCount: Int { lock.withLock { bootstraps } }

    func run(_ executable: String, _ arguments: [String]) async -> CommandResult {
        lock.withLock {
            switch arguments.first {
            case "print":
                prints += 1
                return CommandResult(exitCode: prints <= stillLoaded ? 0 : 113, output: "")
            case "bootstrap":
                bootstraps += 1
                return CommandResult(exitCode: bootstraps <= failingBootstraps ? 5 : 0, output: "Bootstrap failed: 5")
            default:
                return CommandResult(exitCode: 0, output: "")
            }
        }
    }
}

struct BootstrapRetryTests {
    private func agent(_ runner: UnloadingLaunchd) throws -> DaemonLaunchAgent {
        let root = FileManager.default.temporaryDirectory.appending(path: "tasker-boot-\(UUID().uuidString)")
        let paths = DaemonPaths(dataDirectory: root, launchAgentsDirectory: root.appending(path: "agents"), label: "com.technotronika.tasker.test")
        var agent = DaemonLaunchAgent(paths: paths, runner: runner, userID: 501)
        agent.bootstrapAttempts = 3
        return agent
    }

    @Test func waitsForTheOldServiceToUnloadBeforeBootstrap() async throws {
        let runner = UnloadingLaunchd(stillLoaded: 3, failingBootstraps: 0)
        try await agent(runner).start(program: "/a/tasker-mcpd")
        #expect(runner.bootstrapCount == 1)
    }

    @Test func retriesABootstrapThatRacesWithTheUnload() async throws {
        let runner = UnloadingLaunchd(stillLoaded: 0, failingBootstraps: 2)
        try await agent(runner).start(program: "/a/tasker-mcpd")
        #expect(runner.bootstrapCount == 3)
    }

    @Test func reportsABootstrapThatKeepsFailing() async throws {
        let runner = UnloadingLaunchd(stillLoaded: 0, failingBootstraps: 9)
        await #expect(throws: DaemonServiceError.self) {
            try await agent(runner).start(program: "/a/tasker-mcpd")
        }
    }
}

@MainActor
struct ServiceRepairTests {
    @Test func movedAppRewritesTheServiceAndTheLink() async throws {
        let stand = try Stand()
        try stand.integration.installLink()
        try await stand.integration.agent.start(program: "/old/Tasker.app/Contents/Helpers/tasker-mcpd")
        try stand.fileManager.removeItem(at: stand.paths.commandLink)
        try stand.fileManager.createSymbolicLink(
            at: stand.paths.commandLink, withDestinationURL: URL(fileURLWithPath: "/old/Tasker.app/Contents/Helpers/tasker"))

        let report = await stand.integration.reconcile()

        #expect(!report.needsCommandPrompt)
        #expect(stand.link(stand.paths.commandLink) == stand.paths.console.path)
        #expect(stand.integration.agent.isCurrent(program: stand.paths.daemon.path))
    }

    @Test func currentServiceIsNotRestarted() async throws {
        let stand = try Stand()
        try await stand.integration.agent.start(program: stand.paths.daemon.path)
        let before = stand.launchd.calls.filter { $0.contains("bootstrap") }.count

        _ = await stand.integration.reconcile()

        #expect(stand.launchd.calls.filter { $0.contains("bootstrap") }.count == before)
    }

    @Test func noServiceIsCreatedByRepair() async throws {
        let stand = try Stand()
        #expect(try await !stand.integration.agent.repair(program: stand.paths.daemon.path))
        #expect(!stand.fileManager.fileExists(atPath: stand.daemonPaths.plistFile.path))
    }

    @Test func plistWrittenByTheConsoleCountsAsCurrent() throws {
        let stand = try Stand()
        let plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
        <key>ProcessType</key><string>Background</string>
        <key>RunAtLoad</key><true/>
        <key>Label</key><string>\(stand.daemonPaths.label)</string>
        <key>ProgramArguments</key><array><string>\(stand.paths.daemon.path)</string><string>--detached</string></array>
        <key>KeepAlive</key><dict><key>PathState</key><dict><key>\(stand.paths.daemon.path)</key><true/></dict></dict>
        <key>StandardOutPath</key><string>\(stand.daemonPaths.launchLog.path)</string>
        <key>StandardErrorPath</key><string>\(stand.daemonPaths.launchLog.path)</string>
        </dict></plist>
        """
        try plist.write(to: stand.daemonPaths.plistFile, atomically: true, encoding: .utf8)
        #expect(stand.integration.agent.isCurrent(program: stand.paths.daemon.path))
    }
}

@MainActor
struct CommandInstallTests {
    private func controller(_ stand: Stand, prompter: FakePrompter, defaults: UserDefaults) -> AppIntegrationController {
        AppIntegrationController(
            location: .installed, integration: stand.integration, prompter: prompter, defaults: defaults,
            localizer: { Localizer(choice: .ru) })
    }

    private func freshDefaults() -> UserDefaults {
        UserDefaults(suiteName: "tasker-install-\(UUID().uuidString)")!
    }

    @Test func firstLaunchAsksOnceAndRemembersTheRefusal() async throws {
        let stand = try Stand()
        let prompter = FakePrompter()
        let defaults = freshDefaults()
        let model = controller(stand, prompter: prompter, defaults: defaults)

        await model.prepare()
        #expect(prompter.offers.count == 1)
        #expect(CommandLinkState.read(stand.paths) == .missing)

        await model.prepare()
        #expect(prompter.offers.count == 1)

        prompter.choice = CommandChoice(addToPath: false, connectCompletion: false)
        await model.installCommandFromMenu()
        #expect(CommandLinkState.read(stand.paths) == .current)
    }

    @Test func acceptedOfferAddsMarkedBlocksOnlyWhenChosen() async throws {
        let stand = try Stand()
        let prompter = FakePrompter()
        prompter.choice = CommandChoice(addToPath: true, connectCompletion: true)
        try Data("export A=1\n".utf8).write(to: stand.home.appending(path: ".zshrc"))
        let model = controller(stand, prompter: prompter, defaults: freshDefaults())

        await model.prepare()

        let zprofile = try String(contentsOf: stand.home.appending(path: ".zprofile"), encoding: .utf8)
        #expect(zprofile.contains(ShellBlock.pathBegin))
        #expect(zprofile.contains("export PATH=\"\(stand.paths.commandDirectory.path):$PATH\""))
        let zshrc = try String(contentsOf: stand.home.appending(path: ".zshrc"), encoding: .utf8)
        #expect(zshrc.hasPrefix("export A=1\n\n"))
        #expect(zshrc.contains(ShellBlock.completionBegin))
        #expect(stand.fileManager.fileExists(atPath: stand.home.appending(path: ".zshrc.tasker-backup").path))
        let script = try String(contentsOf: stand.paths.completionsDirectory.appending(path: "_tasker"), encoding: .utf8)
        #expect(script == "script of zsh\n")
        #expect(prompter.informed == [.installDoneTitle])
    }

    @Test func refusedBlocksLeaveShellFilesAlone() async throws {
        let stand = try Stand()
        let prompter = FakePrompter()
        prompter.choice = CommandChoice(addToPath: false, connectCompletion: false)
        let model = controller(stand, prompter: prompter, defaults: freshDefaults())

        await model.prepare()

        #expect(!stand.fileManager.fileExists(atPath: stand.home.appending(path: ".zprofile").path))
        #expect(!stand.fileManager.fileExists(atPath: stand.home.appending(path: ".zshrc").path))
    }

    @Test func foreignCommandIsNotPromptedAtLaunchButOfferedFromTheMenu() async throws {
        let stand = try Stand()
        try stand.fileManager.createDirectory(at: stand.paths.commandDirectory, withIntermediateDirectories: true)
        try stand.fileManager.createSymbolicLink(at: stand.paths.commandLink, withDestinationURL: URL(fileURLWithPath: "/opt/other/tasker"))
        let prompter = FakePrompter()
        let model = controller(stand, prompter: prompter, defaults: freshDefaults())

        await model.prepare()
        #expect(prompter.offers.isEmpty)

        await model.installCommandFromMenu()
        #expect(prompter.offers.first?.replaces == "/opt/other/tasker")
        #expect(stand.link(stand.paths.commandLink) == "/opt/other/tasker")
    }

    @Test func completionsRefreshOnlyForANewVersion() async throws {
        let stand = try Stand()
        try stand.fileManager.createDirectory(at: stand.paths.completionsDirectory, withIntermediateDirectories: true)
        await stand.integration.refreshCompletions()
        let first = stand.launchd.calls.filter { $0[0].hasSuffix("/tasker") }.count
        #expect(first == 2)
        await stand.integration.refreshCompletions()
        #expect(stand.launchd.calls.filter { $0[0].hasSuffix("/tasker") }.count == first)
    }

    @Test func removalClearsWhatTheAppAddedAndKeepsTheRest() async throws {
        let stand = try Stand()
        let prompter = FakePrompter()
        prompter.choice = CommandChoice(addToPath: true, connectCompletion: true)
        try Data("export A=1\n".utf8).write(to: stand.home.appending(path: ".zshrc"))
        let model = controller(stand, prompter: prompter, defaults: freshDefaults())
        await model.prepare()
        try await stand.integration.agent.start(program: stand.paths.daemon.path)
        let settings = stand.daemonPaths.dataDirectory.appending(path: "settings.json")
        try Data("{}".utf8).write(to: settings)
        var stopped = false
        model.stopConnection = { stopped = true }

        await model.removeFromMenu()

        #expect(stopped)
        #expect(prompter.quitCalls == 1)
        #expect(CommandLinkState.read(stand.paths) == .missing)
        #expect(!stand.fileManager.fileExists(atPath: stand.daemonPaths.plistFile.path))
        #expect(try String(contentsOf: stand.home.appending(path: ".zshrc"), encoding: .utf8) == "export A=1\n")
        #expect(!stand.fileManager.fileExists(atPath: stand.paths.completionsDirectory.path))
        #expect(stand.fileManager.fileExists(atPath: settings.path))
    }

    @Test func developmentBuildOnlyExplains() async throws {
        let prompter = FakePrompter()
        let model = AppIntegrationController(
            location: .development, integration: nil, prompter: prompter, defaults: freshDefaults(),
            localizer: { Localizer(choice: .ru) })
        await model.prepare()
        #expect(prompter.informed.isEmpty)
        await model.installCommandFromMenu()
        await model.removeFromMenu()
        #expect(prompter.informed == [.installUnavailableTitle, .installUnavailableTitle])
    }
}

struct ShellProfileTests {
    private func temporaryFile() -> URL {
        FileManager.default.temporaryDirectory.appending(path: "tasker-profile-\(UUID().uuidString)")
    }

    @Test func removingBlockRestoresTheFile() throws {
        let file = temporaryFile()
        try Data("line\n".utf8).write(to: file)
        let block = ShellBlock.path(directory: URL(fileURLWithPath: "/h/.local/bin"))
        try ShellProfile.add(block, to: file)
        try ShellProfile.add(block, to: file)
        #expect(try String(contentsOf: file, encoding: .utf8).components(separatedBy: block.begin).count == 2)

        #expect(try ShellProfile.remove(begin: block.begin, end: block.end, mentioning: "/h/.local/bin", from: file))
        #expect(try String(contentsOf: file, encoding: .utf8) == "line\n")
    }

    @Test func blockOfAnotherInstallationStays() throws {
        let file = temporaryFile()
        let block = ShellBlock.path(directory: URL(fileURLWithPath: "/other/bin"))
        try ShellProfile.add(block, to: file)
        #expect(try !ShellProfile.remove(begin: block.begin, end: block.end, mentioning: "/h/.local/bin", from: file))
    }

    @Test func profileFollowsTheShell() {
        let home = URL(fileURLWithPath: "/nonexistent-home")
        #expect(ShellProfile(home: home, shellPath: "/bin/zsh").pathFile.lastPathComponent == ".zprofile")
        #expect(ShellProfile(home: home, shellPath: "/bin/zsh").completionFile?.lastPathComponent == ".zshrc")
        #expect(ShellProfile(home: home, shellPath: "/bin/bash").pathFile.lastPathComponent == ".bash_profile")
        #expect(ShellProfile(home: home, shellPath: "/usr/bin/fish").completionFile == nil)
    }

    @Test func pathCheckMatchesWholeEntries() {
        let directory = URL(fileURLWithPath: "/h/.local/bin")
        #expect(LoginShell.contains(directory, path: "/usr/bin:/h/.local/bin"))
        #expect(!LoginShell.contains(directory, path: "/usr/bin:/h/.local/bin2"))
    }
}
