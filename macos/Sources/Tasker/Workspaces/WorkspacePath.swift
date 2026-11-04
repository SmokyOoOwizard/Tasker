import Darwin
import Foundation

enum WorkspacePath {
    static let dataFolder = ".tasker"

    static func canonical(_ path: String) -> String {
        var resolved = resolve(path)
        let url = URL(fileURLWithPath: resolved)
        if url.lastPathComponent.lowercased() == dataFolder {
            resolved = url.deletingLastPathComponent().path
        }
        return resolved
    }

    static func same(_ first: String, _ second: String) -> Bool {
        canonical(first) == canonical(second)
    }

    static func name(_ path: String) -> String {
        let last = URL(fileURLWithPath: path).lastPathComponent
        return last.isEmpty ? path : last
    }

    static func hasData(_ path: String, fileManager: FileManager = .default) -> Bool {
        var isDirectory: ObjCBool = false
        let exists = fileManager.fileExists(atPath: URL(fileURLWithPath: path).appending(path: dataFolder).path, isDirectory: &isDirectory)
        return exists && isDirectory.boolValue
    }

    private static func resolve(_ path: String) -> String {
        let expanded = (path as NSString).expandingTildeInPath
        if let real = realpath(expanded, nil) {
            defer { free(real) }
            return String(cString: real)
        }
        return URL(fileURLWithPath: expanded).standardizedFileURL.path
    }
}
