import Foundation

struct ShellBlock: Equatable, Sendable {
    var begin: String
    var end: String
    var body: [String]

    var lines: [String] { [begin] + body + [end] }

    static func completion(shell: String, directory: URL) -> ShellBlock? {
        let header = "# Tab completion for tasker, added by Tasker.app. To undo: delete this block or use the Tasker menu."
        let body: [String]
        switch shell {
        case "zsh":
            let file = directory.appending(path: "_tasker").path
            body = [
                header,
                "if [ -f \"\(file)\" ]; then",
                "  fpath=(\"\(directory.path)\" $fpath)",
                "  autoload -Uz compinit _tasker",
                "  (( $+functions[compdef] )) || compinit",
                "  compdef _tasker tasker",
                "fi"
            ]
        case "bash":
            let file = directory.appending(path: "tasker.bash").path
            body = [header, "if [ -f \"\(file)\" ]; then . \"\(file)\"; fi"]
        default:
            return nil
        }
        return ShellBlock(begin: completionBegin, end: completionEnd, body: body)
    }

    static func path(directory: URL) -> ShellBlock {
        ShellBlock(
            begin: pathBegin,
            end: pathEnd,
            body: ["# The tasker command, added by Tasker.app.", "export PATH=\"\(directory.path):$PATH\""]
        )
    }

    static let completionBegin = "# >>> tasker completion >>>"
    static let completionEnd = "# <<< tasker completion <<<"
    static let pathBegin = "# >>> tasker path >>>"
    static let pathEnd = "# <<< tasker path <<<"
}

struct ShellProfile: Sendable {
    static let backupSuffix = ".tasker-backup"

    var home: URL
    var shell: String

    init(home: URL, shellPath: String?) {
        self.home = home
        shell = shellPath.map { ($0 as NSString).lastPathComponent } ?? ""
    }

    var supportsCompletion: Bool { shell == "zsh" || shell == "bash" }

    var completionFile: URL? {
        switch shell {
        case "zsh": home.appending(path: ".zshrc")
        case "bash": bashLoginFile
        default: nil
        }
    }

    var pathFile: URL {
        shell == "bash" ? bashLoginFile : home.appending(path: ".zprofile")
    }

    var candidates: [URL] {
        [".zshrc", ".zprofile", ".bash_profile", ".bash_login", ".profile", ".bashrc"].map { home.appending(path: $0) }
    }

    private var bashLoginFile: URL {
        for name in [".bash_profile", ".bash_login", ".profile"] {
            let file = home.appending(path: name)
            if FileManager.default.fileExists(atPath: file.path) { return file }
        }
        return home.appending(path: ".bash_profile")
    }

    static func contains(_ block: ShellBlock, in file: URL) -> Bool {
        let lines = read(file)
        return lines.contains(block.begin) || lines.contains(block.end)
    }

    static func add(_ block: ShellBlock, to file: URL) throws {
        if contains(block, in: file) { return }
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: file.path) {
            let backup = URL(fileURLWithPath: file.path + backupSuffix)
            if !fileManager.fileExists(atPath: backup.path) {
                try fileManager.copyItem(at: file, to: backup)
            }
        }
        var text = (try? String(contentsOf: file, encoding: .utf8)) ?? ""
        if !text.isEmpty {
            if !text.hasSuffix("\n") { text += "\n" }
            text += "\n"
        }
        text += block.lines.joined(separator: "\n") + "\n"
        try Data(text.utf8).write(to: file)
    }

    @discardableResult
    static func remove(begin: String, end: String, mentioning needle: String, from file: URL) throws -> Bool {
        var lines = read(file)
        guard let first = lines.firstIndex(of: begin), let last = lines.firstIndex(of: end), first < last else { return false }
        guard lines[first...last].contains(where: { $0.contains(needle) }) else { return false }
        lines.removeSubrange(first...last)
        if first > 0, first - 1 < lines.count, lines[first - 1].isEmpty {
            lines.remove(at: first - 1)
        }
        var text = lines.joined(separator: "\n")
        if !text.isEmpty, !text.hasSuffix("\n") { text += "\n" }
        try Data(text.utf8).write(to: file)
        return true
    }

    private static func read(_ file: URL) -> [String] {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return [] }
        var lines = text.components(separatedBy: "\n")
        if lines.last == "" { lines.removeLast() }
        return lines
    }
}

enum LoginShell {
    static let marker = "__TASKER_PATH__"

    static func path(shellPath: String, home: URL, timeout: TimeInterval = 5) async -> String? {
        await Task.detached {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: shellPath)
            process.arguments = ["-ilc", "printf '\(marker)%s\(marker)' \"$PATH\""]
            var environment = ProcessInfo.processInfo.environment
            environment["HOME"] = home.path
            process.environment = environment
            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = FileHandle.nullDevice
            process.standardInput = FileHandle.nullDevice
            do {
                try process.run()
            } catch {
                return nil
            }
            let deadline = Date().addingTimeInterval(timeout)
            while process.isRunning, Date() < deadline {
                try? await Task.sleep(for: .milliseconds(50))
            }
            if process.isRunning {
                process.terminate()
                return nil
            }
            let output = String(decoding: pipe.fileHandleForReading.availableData, as: UTF8.self)
            let parts = output.components(separatedBy: marker)
            return parts.count >= 3 ? parts[1] : nil
        }.value
    }

    static func contains(_ directory: URL, path: String) -> Bool {
        path.split(separator: ":").contains { $0 == directory.path || $0 == directory.path + "/" }
    }
}
