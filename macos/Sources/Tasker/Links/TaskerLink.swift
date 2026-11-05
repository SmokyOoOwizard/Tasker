import CryptoKit
import Foundation

enum WorkspaceLinkID {
    static let hashLength = 6

    static func make(path: String) -> String {
        let canonical = WorkspacePath.canonical(path)
        let digest = SHA256.hash(data: Data(canonical.utf8))
        let hash = digest.map { String(format: "%02x", $0) }.joined().prefix(hashLength)
        return normalized(WorkspacePath.name(canonical)) + "-" + hash
    }

    static func normalized(_ name: String) -> String {
        name.precomposedStringWithCanonicalMapping
    }

    static func matches(_ id: String, path: String) -> Bool {
        make(path: path) == normalized(id)
    }
}

enum TaskerLinkTarget: Equatable, Sendable {
    case workspace
    case task(reference: String)
    case taskID(UUID)
    case board(UUID)
    case series(prefix: String)
}

struct TaskerLink: Equatable, Sendable {
    static let scheme = "tasker"
    static let maxLength = 512

    var workspace: String
    var target: TaskerLinkTarget

    init(workspace: String, target: TaskerLinkTarget) {
        self.workspace = workspace
        self.target = target
    }

    init(path: String, target: TaskerLinkTarget) {
        self.init(workspace: WorkspaceLinkID.make(path: path), target: target)
    }

    var string: String {
        var result = Self.scheme + "://" + Self.encode(workspace)
        switch target {
        case .workspace: break
        case .task(let reference): result += "/" + Self.encode(reference)
        case .taskID(let id): result += "/task/" + id.uuidString.lowercased()
        case .board(let id): result += "/board/" + id.uuidString.lowercased()
        case .series(let prefix): result += "/series/" + Self.encode(prefix)
        }
        return result
    }

    var url: URL? { URL(string: string) }

    static func parse(_ url: URL) -> TaskerLink? {
        parse(url.absoluteString)
    }

    static func parse(_ text: String) -> TaskerLink? {
        guard text.utf8.count <= maxLength else { return nil }
        let prefix = scheme + "://"
        guard text.lowercased().hasPrefix(prefix) else { return nil }
        let rest = String(text.dropFirst(prefix.count))
        guard !rest.contains(where: { "?#@:\\".contains($0) || $0.isWhitespace || $0.isNewline }) else { return nil }
        guard rest.unicodeScalars.allSatisfy({ $0.value >= 0x20 && $0.value != 0x7f }) else { return nil }
        var segments = rest.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        if segments.last == "", segments.count > 1 { segments.removeLast() }
        guard let head = segments.first, let workspace = decode(head), isWorkspaceID(workspace) else { return nil }
        let tail = Array(segments.dropFirst())
        switch tail.count {
        case 0:
            return TaskerLink(workspace: workspace, target: .workspace)
        case 1:
            guard let reference = decode(tail[0]), isReference(reference) else { return nil }
            return TaskerLink(workspace: workspace, target: .task(reference: reference))
        case 2:
            switch tail[0].lowercased() {
            case "task":
                guard let id = UUID(uuidString: tail[1]) else { return nil }
                return TaskerLink(workspace: workspace, target: .taskID(id))
            case "board":
                guard let id = UUID(uuidString: tail[1]) else { return nil }
                return TaskerLink(workspace: workspace, target: .board(id))
            case "series":
                guard let prefix = decode(tail[1]), isPrefix(prefix) else { return nil }
                return TaskerLink(workspace: workspace, target: .series(prefix: prefix))
            default:
                return nil
            }
        default:
            return nil
        }
    }

    static func isWorkspaceID(_ value: String) -> Bool {
        guard value.count > hashLengthWithDash, value.count <= 255 else { return false }
        let suffix = value.suffix(hashLengthWithDash)
        guard suffix.first == "-", suffix.dropFirst().allSatisfy({ $0.isASCII && $0.isHexDigit }) else { return false }
        let name = value.dropLast(hashLengthWithDash)
        return !name.contains("/") && !name.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }

    static func isReference(_ value: String) -> Bool {
        let parts = value.split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2, isPrefix(String(parts[0])) else { return false }
        return !parts[1].isEmpty && parts[1].count <= 9 && parts[1].allSatisfy { $0.isASCII && $0.isNumber }
    }

    static func isPrefix(_ value: String) -> Bool {
        !value.isEmpty && value.count <= 32 && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") }
    }

    private static let hashLengthWithDash = WorkspaceLinkID.hashLength + 1

    private static let allowed: CharacterSet = {
        var set = CharacterSet.alphanumerics
        set.insert(charactersIn: "-._~")
        return set.intersection(CharacterSet(charactersIn: Unicode.Scalar(0)...Unicode.Scalar(127)))
    }()

    private static func encode(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }

    private static func decode(_ value: String) -> String? {
        guard !value.isEmpty, let decoded = value.removingPercentEncoding else { return nil }
        guard !decoded.isEmpty, !decoded.contains("/"), !decoded.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return nil }
        return decoded
    }
}
