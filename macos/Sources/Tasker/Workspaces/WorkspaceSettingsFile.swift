import Darwin
import Foundation

struct WorkspaceSettingsError: Error, Equatable, LocalizedError {
    var message: String

    var errorDescription: String? { message }
}

struct WorkspaceSettingsFile: Sendable {
    static let maxUserNameLength = 100

    var directory: URL

    var file: URL { directory.appending(path: "settings.json") }
    var lock: URL { directory.appending(path: "settings.lock") }

    func workspacePaths() throws -> [String] {
        guard let root = try readRoot() else { return [] }
        return entries(in: root).compactMap { $0["path"] as? String }
    }

    func userName() throws -> String? {
        guard let root = try readRoot() else { return nil }
        guard let name = (root["userName"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty else { return nil }
        return name
    }

    func setUserName(_ name: String?) throws {
        let value = name?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let value, value.count > Self.maxUserNameLength { throw WorkspaceSettingsError(message: "\(value.count)") }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try withLock {
            var root = try readRoot() ?? [:]
            if let value, !value.isEmpty { root["userName"] = value } else { root.removeValue(forKey: "userName") }
            try write(root)
        }
    }

    @discardableResult
    func add(_ path: String) throws -> Bool {
        let canonical = WorkspacePath.canonical(path)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return try withLock {
            var root = try readRoot() ?? [:]
            var mcp = root["mcp"] as? [String: Any] ?? [:]
            var workspaces = mcp["workspaces"] as? [[String: Any]] ?? []
            if workspaces.contains(where: { ($0["path"] as? String).map { WorkspacePath.same($0, canonical) } ?? false }) {
                return false
            }
            workspaces.append(["kind": "files", "path": canonical])
            mcp["workspaces"] = workspaces
            root["mcp"] = mcp
            try write(root)
            return true
        }
    }

    private func write(_ root: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try (data + Data("\n".utf8)).write(to: file, options: .atomic)
    }

    private func entries(in root: [String: Any]) -> [[String: Any]] {
        ((root["mcp"] as? [String: Any])?["workspaces"] as? [[String: Any]]) ?? []
    }

    private func readRoot() throws -> [String: Any]? {
        guard let data = try? Data(contentsOf: file) else { return nil }
        if data.allSatisfy({ $0 == 0x20 || $0 == 0x0A || $0 == 0x0D || $0 == 0x09 }) { return nil }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw WorkspaceSettingsError(message: file.path)
        }
        return object
    }

    private func withLock<T>(_ body: () throws -> T) throws -> T {
        let descriptor = open(lock.path, O_CREAT | O_RDWR, 0o600)
        guard descriptor >= 0 else { throw WorkspaceSettingsError(message: String(cString: strerror(errno))) }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX) == 0 else { throw WorkspaceSettingsError(message: String(cString: strerror(errno))) }
        defer { flock(descriptor, LOCK_UN) }
        return try body()
    }
}
