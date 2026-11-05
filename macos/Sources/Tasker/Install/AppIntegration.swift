import Foundation

struct IntegrationReport: Equatable, Sendable {
    var migrated = false
    var migrationError: String?
    var needsCommandPrompt = false
}

struct AppIntegration: Sendable {
    var paths: InstallPaths
    var daemonPaths: DaemonPaths
    var legacyLabel: String?
    var version: String
    var agent: DaemonLaunchAgent
    var transport: any DaemonTransport
    var runner: any CommandRunning
    var shellPath: String?
    var timings = DaemonTimings()
    var sleep: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }

    private var fileManager: FileManager { .default }
    private var files: DaemonFiles { DaemonFiles(paths: daemonPaths) }
    private var userDomain: String { "gui/\(agent.userID)" }

    var legacy: LegacyInstallation? {
        guard let legacyLabel else { return nil }
        return LegacyInstallation.find(home: paths.home, agents: daemonPaths.launchAgentsDirectory, label: legacyLabel)
    }

    var profile: ShellProfile { ShellProfile(home: paths.home, shellPath: shellPath) }

    func reconcile() async -> IntegrationReport {
        var report = IntegrationReport()
        removePreviousProgramsIfSwitched()
        if let legacy {
            do {
                try await migrate(legacy)
                report.migrated = true
            } catch {
                report.migrationError = Self.describe(error)
                return report
            }
        }
        _ = try? await agent.repair(program: paths.daemon.path)
        repairLegacyShims()
        switch CommandLinkState.read(paths) {
        case .stale:
            try? installLink()
        case .missing:
            report.needsCommandPrompt = true
        case .current, .foreign:
            break
        }
        await refreshCompletions()
        return report
    }

    func migrate(_ legacy: LegacyInstallation) async throws {
        let hadPlist = fileManager.fileExists(atPath: legacy.plist.path)
        if hadPlist {
            _ = await runner.run("/bin/launchctl", ["bootout", "\(userDomain)/\(legacy.label)"])
        }
        do {
            try await stopRunningDaemon()
            var migrating = agent
            migrating.conflictingPlist = nil
            try await migrating.start(program: paths.daemon.path)
            try await waitUntilAnswering()
        } catch {
            try? await agent.remove()
            if hadPlist {
                _ = await runner.run("/bin/launchctl", ["bootstrap", userDomain, legacy.plist.path])
            }
            throw error
        }
        if hadPlist { try? fileManager.removeItem(at: legacy.plist) }
        if let target = try? fileManager.destinationOfSymbolicLink(atPath: legacy.link.path),
           target == legacy.app.appending(path: "tasker").path {
            try relink(legacy.link, to: paths.console)
        }
        replaceProgramsWithShims(legacy)
    }

    private func stopRunningDaemon() async throws {
        if files.isRunning(), let info = files.readInfo() {
            try? await transport.stop(info: info)
        }
        let clock = ContinuousClock()
        let deadline = clock.now + .seconds(15)
        while files.isRunning(), clock.now < deadline {
            try? await sleep(timings.pollWhileStarting)
        }
        if files.isRunning() {
            throw DaemonServiceError(message: "the previous MCP server did not stop")
        }
    }

    private func waitUntilAnswering() async throws {
        let clock = ContinuousClock()
        let deadline = clock.now + timings.startTimeout
        while clock.now < deadline {
            if files.isRunning(), let info = files.readInfo(), (try? await transport.status(of: info)) != nil { return }
            try? await sleep(timings.pollWhileStarting)
        }
        throw DaemonServiceError(message: "the MCP server of the app did not answer")
    }

    private func replaceProgramsWithShims(_ legacy: LegacyInstallation) {
        guard fileManager.fileExists(atPath: legacy.app.path) else { return }
        try? fileManager.removeItem(at: legacy.previous)
        guard (try? fileManager.moveItem(at: legacy.app, to: legacy.previous)) != nil else { return }
        try? fileManager.createDirectory(at: legacy.app, withIntermediateDirectories: true)
        writeShims(in: legacy.app)
    }

    private func writeShims(in directory: URL) {
        try? relink(directory.appending(path: "tasker"), to: paths.console)
        try? relink(directory.appending(path: "tasker-mcpd"), to: paths.daemon)
    }

    private var defaultLegacyApp: URL { paths.home.appending(path: ".local/share/tasker/app") }

    private func repairLegacyShims() {
        let console = defaultLegacyApp.appending(path: "tasker").path
        guard let target = try? fileManager.destinationOfSymbolicLink(atPath: console), target != paths.console.path else { return }
        writeShims(in: defaultLegacyApp)
    }

    private func removePreviousProgramsIfSwitched() {
        let previous = paths.home.appending(path: ".local/share/tasker/app.previous")
        guard legacy == nil, fileManager.fileExists(atPath: previous.path) else { return }
        try? fileManager.removeItem(at: previous)
    }

    func installLink() throws {
        try relink(paths.commandLink, to: paths.console)
    }

    private func relink(_ link: URL, to target: URL) throws {
        try fileManager.createDirectory(at: link.deletingLastPathComponent(), withIntermediateDirectories: true)
        if (try? fileManager.destinationOfSymbolicLink(atPath: link.path)) != nil || fileManager.fileExists(atPath: link.path) {
            try fileManager.removeItem(at: link)
        }
        try fileManager.createSymbolicLink(at: link, withDestinationURL: target)
    }

    func isCommandOnPath() async -> Bool? {
        guard let shellPath, let path = await LoginShell.path(shellPath: shellPath, home: paths.home) else { return nil }
        return LoginShell.contains(paths.commandDirectory, path: path)
    }

    func addToPath() throws {
        try ShellProfile.add(.path(directory: paths.commandDirectory), to: profile.pathFile)
    }

    var isCompletionConnected: Bool {
        guard let file = profile.completionFile,
              let block = ShellBlock.completion(shell: profile.shell, directory: paths.completionsDirectory) else { return false }
        return ShellProfile.contains(block, in: file)
    }

    func connectCompletion() async throws {
        await refreshCompletions(force: true)
        guard let file = profile.completionFile,
              let block = ShellBlock.completion(shell: profile.shell, directory: paths.completionsDirectory) else { return }
        try ShellProfile.add(block, to: file)
    }

    func refreshCompletions(force: Bool = false) async {
        let directory = paths.completionsDirectory
        guard force || fileManager.fileExists(atPath: directory.path) else { return }
        if !force, (try? String(contentsOf: paths.completionsStamp, encoding: .utf8)) == version { return }
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        for (shell, name) in [("zsh", "_tasker"), ("bash", "tasker.bash")] {
            let result = await runner.run(paths.console.path, ["completion", shell])
            guard result.succeeded, !result.output.isEmpty else { return }
            try? Data(result.output.utf8).write(to: directory.appending(path: name), options: .atomic)
        }
        try? Data(version.utf8).write(to: paths.completionsStamp, options: .atomic)
    }

    func removeAll() async throws {
        try await agent.remove()
        try? await stopRunningDaemon()
        if CommandLinkState.read(paths) == .current {
            try fileManager.removeItem(at: paths.commandLink)
        }
        let shimConsole = defaultLegacyApp.appending(path: "tasker").path
        if (try? fileManager.destinationOfSymbolicLink(atPath: shimConsole)) != nil {
            try? fileManager.removeItem(at: defaultLegacyApp)
        }
        try? fileManager.removeItem(at: paths.home.appending(path: ".local/share/tasker/app.previous"))
        for file in profile.candidates {
            _ = try? ShellProfile.remove(
                begin: ShellBlock.completionBegin, end: ShellBlock.completionEnd,
                mentioning: paths.completionsDirectory.path, from: file)
            _ = try? ShellProfile.remove(
                begin: ShellBlock.pathBegin, end: ShellBlock.pathEnd,
                mentioning: paths.commandDirectory.path, from: file)
        }
        try? fileManager.removeItem(at: paths.completionsDirectory)
        try? fileManager.removeItem(at: paths.home.appending(path: ".local/share/tasker"))
    }

    private static func describe(_ error: any Error) -> String {
        (error as? DaemonServiceError)?.message ?? error.localizedDescription
    }
}
