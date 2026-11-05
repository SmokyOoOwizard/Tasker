import Darwin
import Foundation

struct DaemonPaths: Sendable {
    var dataDirectory: URL
    var launchAgentsDirectory: URL
    var label: String

    static let defaultLabel = "com.technotronika.tasker.daemon"

    init(dataDirectory: URL, launchAgentsDirectory: URL, label: String = DaemonPaths.defaultLabel) {
        self.dataDirectory = dataDirectory
        self.launchAgentsDirectory = launchAgentsDirectory
        self.label = label
    }

    static func installHome(environment: [String: String], home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        if let root = environment[InstallLocation.rootVariable], !root.isEmpty { return URL(fileURLWithPath: root) }
        return home
    }

    init(environment: [String: String] = ProcessInfo.processInfo.environment, home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        let home = Self.installHome(environment: environment, home: home)
        let data: URL
        if let override = environment["TASKER_HOME"], !override.isEmpty {
            data = URL(fileURLWithPath: (override as NSString).expandingTildeInPath)
        } else {
            data = home.appending(path: "Library/Application Support/Tasker")
        }
        let agents: URL
        if let override = environment["TASKER_SERVICE_DIR"], !override.isEmpty {
            agents = URL(fileURLWithPath: override)
        } else {
            agents = home.appending(path: "Library/LaunchAgents")
        }
        let label = environment["TASKER_SERVICE_LABEL"].flatMap { $0.isEmpty ? nil : $0 } ?? Self.defaultLabel
        self.init(dataDirectory: data, launchAgentsDirectory: agents, label: label)
    }

    var daemonDirectory: URL { dataDirectory.appending(path: "mcp") }
    var lockFile: URL { daemonDirectory.appending(path: "daemon.lock") }
    var infoFile: URL { daemonDirectory.appending(path: "daemon.json") }
    var logsDirectory: URL { dataDirectory.appending(path: "logs") }
    var plistFile: URL { launchAgentsDirectory.appending(path: label + ".plist") }
    var launchLog: URL { logsDirectory.appending(path: "mcp-launchd.log") }
}

struct DaemonFiles: Sendable {
    var paths: DaemonPaths

    func readInfo() -> DaemonInfo? {
        guard let data = try? Data(contentsOf: paths.infoFile) else { return nil }
        return try? JSONDecoder().decode(DaemonInfo.self, from: data)
    }

    func isRunning() -> Bool {
        let descriptor = open(paths.lockFile.path, O_RDWR)
        guard descriptor >= 0 else { return false }
        defer { close(descriptor) }
        if flock(descriptor, LOCK_EX | LOCK_NB) == 0 {
            flock(descriptor, LOCK_UN)
            return false
        }
        return errno == EWOULDBLOCK
    }
}
