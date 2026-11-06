import Darwin
import Foundation

struct CommandResult: Equatable, Sendable {
    var exitCode: Int32
    var output: String

    var succeeded: Bool { exitCode == 0 }
}

protocol CommandRunning: Sendable {
    func run(_ executable: String, _ arguments: [String]) async -> CommandResult
}

struct SystemCommandRunner: CommandRunning {
    func run(_ executable: String, _ arguments: [String]) async -> CommandResult {
        await Task.detached {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = pipe
            do {
                try process.run()
            } catch {
                return CommandResult(exitCode: 127, output: error.localizedDescription)
            }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return CommandResult(exitCode: process.terminationStatus, output: String(decoding: data, as: UTF8.self))
        }.value
    }
}

protocol DaemonService: Sendable {
    func start(program: String) async throws
}

struct DaemonServiceError: Error, Equatable {
    var message: String
}

struct DaemonBuildInfo: Decodable, Equatable, Sendable {
    var version: String
    var build: String

    static func read(program: String, runner: any CommandRunning) async -> DaemonBuildInfo? {
        let result = await runner.run(program, ["--build-info"])
        guard result.succeeded else { return nil }
        return try? JSONDecoder().decode(DaemonBuildInfo.self, from: Data(result.output.utf8))
    }
}

struct DaemonLaunchAgent: DaemonService {
    var paths: DaemonPaths
    var runner: any CommandRunning
    var userID: uid_t = getuid()
    var environment: [String: String] = [:]
    var conflictingPlist: URL?
    var unloadTimeout: Duration = .seconds(25)
    var bootstrapAttempts = 5

    static func live(paths: DaemonPaths, environment: [String: String]) -> DaemonLaunchAgent {
        var serviceEnvironment: [String: String] = [:]
        if let home = environment["TASKER_HOME"], !home.isEmpty {
            serviceEnvironment["TASKER_HOME"] = paths.dataDirectory.path
        }
        var agent = DaemonLaunchAgent(paths: paths, runner: SystemCommandRunner(), environment: serviceEnvironment)
        if let label = LegacyInstallation.label(environment: environment) {
            agent.conflictingPlist = paths.launchAgentsDirectory.appending(path: label + ".plist")
        }
        return agent
    }

    private var domain: String { "gui/\(userID)" }
    private var target: String { "\(domain)/\(paths.label)" }

    func plist(program: String) -> String {
        var lines = [
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "    <key>Label</key><string>\(Self.escape(paths.label))</string>",
            "    <key>ProgramArguments</key>",
            "    <array>",
            "        <string>\(Self.escape(program))</string>",
            "        <string>--detached</string>",
            "    </array>"
        ]
        if !environment.isEmpty {
            lines.append("    <key>EnvironmentVariables</key>")
            lines.append("    <dict>")
            for key in environment.keys.sorted() {
                lines.append("        <key>\(Self.escape(key))</key><string>\(Self.escape(environment[key]!))</string>")
            }
            lines.append("    </dict>")
        }
        lines += [
            "    <key>RunAtLoad</key><true/>",
            "    <key>KeepAlive</key>",
            "    <dict>",
            "        <key>PathState</key>",
            "        <dict>",
            "            <key>\(Self.escape(program))</key><true/>",
            "        </dict>",
            "    </dict>",
            "    <key>ProcessType</key><string>Background</string>",
            "    <key>StandardOutPath</key><string>\(Self.escape(paths.launchLog.path))</string>",
            "    <key>StandardErrorPath</key><string>\(Self.escape(paths.launchLog.path))</string>",
            "</dict>",
            "</plist>",
            ""
        ]
        return lines.joined(separator: "\n")
    }

    func start(program: String) async throws {
        if let conflictingPlist, FileManager.default.fileExists(atPath: conflictingPlist.path) {
            throw DaemonServiceError(message: "the previous service is still installed: \(conflictingPlist.path)")
        }
        let loaded = await runner.run("/bin/launchctl", ["print", target]).succeeded

        if !isCurrent(program: program) {
            try await install(program: program)
        } else if loaded {
            try check(await runner.run("/bin/launchctl", ["kickstart", target]))
        } else {
            try await bootstrap()
        }
    }

    @discardableResult
    func repair(program: String) async throws -> Bool {
        guard FileManager.default.fileExists(atPath: paths.plistFile.path), !isCurrent(program: program) else { return false }
        try await install(program: program)
        return true
    }

    func remove() async throws {
        _ = await runner.run("/bin/launchctl", ["bootout", target])
        if FileManager.default.fileExists(atPath: paths.plistFile.path) {
            try FileManager.default.removeItem(at: paths.plistFile)
        }
    }

    func isCurrent(program: String) -> Bool {
        guard let data = try? Data(contentsOf: paths.plistFile),
              let current = Self.dictionary(data),
              let desired = Self.dictionary(Data(plist(program: program).utf8)) else { return false }
        return current.isEqual(to: desired as! [AnyHashable: Any])
    }

    private func install(program: String) async throws {
        try FileManager.default.createDirectory(at: paths.launchAgentsDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: paths.logsDirectory, withIntermediateDirectories: true)
        try plist(program: program).write(to: paths.plistFile, atomically: true, encoding: .utf8)
        _ = await runner.run("/bin/launchctl", ["bootout", target])
        try await bootstrap()
    }

    private func bootstrap() async throws {
        let clock = ContinuousClock()
        let deadline = clock.now + unloadTimeout
        while clock.now < deadline, await runner.run("/bin/launchctl", ["print", target]).succeeded {
            try await Task.sleep(for: .milliseconds(200))
        }
        var result = CommandResult(exitCode: 1, output: "")
        for attempt in 1...bootstrapAttempts {
            result = await runner.run("/bin/launchctl", ["bootstrap", domain, paths.plistFile.path])
            if result.succeeded || attempt == bootstrapAttempts { break }
            try await Task.sleep(for: .seconds(1))
        }
        try check(result)
    }

    private static func dictionary(_ data: Data) -> NSDictionary? {
        try? PropertyListSerialization.propertyList(from: data, format: nil) as? NSDictionary
    }

    private func check(_ result: CommandResult) throws {
        if !result.succeeded {
            throw DaemonServiceError(message: result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    private static func escape(_ value: String) -> String {
        value
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }
}
