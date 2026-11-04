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

struct DaemonLaunchAgent: DaemonService {
    var paths: DaemonPaths
    var runner: any CommandRunning
    var userID: uid_t = getuid()
    var environment: [String: String] = [:]

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
            "    <key>KeepAlive</key><true/>",
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
        let desired = plist(program: program)
        let current = try? String(contentsOf: paths.plistFile, encoding: .utf8)
        let loaded = await runner.run("/bin/launchctl", ["print", target]).succeeded

        if current != desired {
            try FileManager.default.createDirectory(at: paths.launchAgentsDirectory, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: paths.logsDirectory, withIntermediateDirectories: true)
            try desired.write(to: paths.plistFile, atomically: true, encoding: .utf8)
            _ = await runner.run("/bin/launchctl", ["bootout", target])
            try check(await runner.run("/bin/launchctl", ["bootstrap", domain, paths.plistFile.path]))
        } else if loaded {
            try check(await runner.run("/bin/launchctl", ["kickstart", target]))
        } else {
            try check(await runner.run("/bin/launchctl", ["bootstrap", domain, paths.plistFile.path]))
        }
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
