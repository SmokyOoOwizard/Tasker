import Foundation

enum InstallLocation: Equatable, Sendable {
    case installed
    case temporary
    case development

    static let rootVariable = "TASKER_INSTALL_ROOT"

    static func classify(bundle: String, environment: [String: String], isDebugBuild: Bool) -> InstallLocation {
        if bundle.contains("/AppTranslocation/") || bundle.hasPrefix("/Volumes/") { return .temporary }
        if let root = environment[rootVariable], !root.isEmpty {
            return bundle.hasPrefix(root + "/") ? .installed : .development
        }
        if isDebugBuild { return .development }
        if let home = environment["TASKER_HOME"], !home.isEmpty { return .development }
        if bundle.contains("/Build/Products/") || bundle.contains("/DerivedData/") { return .development }
        return .installed
    }

    static var current: InstallLocation {
        #if DEBUG
        let isDebugBuild = true
        #else
        let isDebugBuild = false
        #endif
        return classify(bundle: Bundle.main.bundleURL.path, environment: ProcessInfo.processInfo.environment, isDebugBuild: isDebugBuild)
    }
}

struct InstallPaths: Equatable, Sendable {
    var bundle: URL
    var home: URL

    var console: URL { bundle.appending(path: "Contents/Helpers/tasker") }
    var daemon: URL { bundle.appending(path: "Contents/Helpers/tasker-mcpd") }
    var commandDirectory: URL { home.appending(path: ".local/bin") }
    var commandLink: URL { commandDirectory.appending(path: "tasker") }
    var completionsDirectory: URL { home.appending(path: ".local/share/tasker/completions") }
    var completionsStamp: URL { completionsDirectory.appending(path: ".version") }
}

enum CommandLinkState: Equatable, Sendable {
    case current
    case missing
    case stale
    case foreign(String)

    static func read(_ paths: InstallPaths, fileManager: FileManager = .default) -> CommandLinkState {
        let link = paths.commandLink.path
        if let target = try? fileManager.destinationOfSymbolicLink(atPath: link) {
            if target == paths.console.path { return .current }
            return target.hasSuffix(".app/Contents/Helpers/tasker") ? .stale : .foreign(target)
        }
        return fileManager.fileExists(atPath: link) ? .foreign(link) : .missing
    }
}

struct LegacyInstallation: Equatable, Sendable {
    static let defaultLabel = "com.tasker.mcp"
    static let labelVariable = "TASKER_LEGACY_LABEL"

    var label: String
    var plist: URL
    var prefix: URL

    var app: URL { prefix.appending(path: "share/tasker/app") }
    var previous: URL { prefix.appending(path: "share/tasker/app.previous") }
    var link: URL { prefix.appending(path: "bin/tasker") }

    static func label(environment: [String: String]) -> String? {
        guard let root = environment[InstallLocation.rootVariable], !root.isEmpty else { return defaultLabel }
        guard let label = environment[labelVariable], !label.isEmpty, label != defaultLabel else { return nil }
        return label
    }

    static func find(home: URL, agents: URL, label: String, fileManager: FileManager = .default) -> LegacyInstallation? {
        let plist = agents.appending(path: label + ".plist")
        let hasPlist = fileManager.fileExists(atPath: plist.path)
        let prefix = (hasPlist ? prefix(ofPlist: plist) : nil) ?? home.appending(path: ".local")
        let legacy = LegacyInstallation(label: label, plist: plist, prefix: prefix)
        if hasPlist { return legacy }

        let console = legacy.app.appending(path: "tasker").path
        let isLink = (try? fileManager.destinationOfSymbolicLink(atPath: console)) != nil
        if !isLink, fileManager.fileExists(atPath: console) { return legacy }
        if let target = try? fileManager.destinationOfSymbolicLink(atPath: legacy.link.path), target == console { return legacy }
        return nil
    }

    private static func prefix(ofPlist plist: URL) -> URL? {
        guard let data = try? Data(contentsOf: plist),
              let dictionary = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let program = (dictionary["ProgramArguments"] as? [String])?.first else { return nil }
        let suffix = "/share/tasker/app/tasker-mcpd"
        guard program.hasSuffix(suffix) else { return nil }
        return URL(fileURLWithPath: String(program.dropLast(suffix.count)))
    }
}
