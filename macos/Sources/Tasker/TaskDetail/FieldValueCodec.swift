import Foundation
import TaskerAPI

enum FieldValueCodec {
    static func normalize(_ text: String, type: FieldType) -> String? {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        switch type {
        case .string:
            return value
        case .int:
            return Int64(value).map(String.init)
        case .float:
            let canonical = value.replacingOccurrences(of: ",", with: ".")
            guard let number = Double(canonical), number.isFinite else { return nil }
            return canonical
        case .bool:
            return value == "true" || value == "false" ? value : nil
        case .date:
            return day(from: value).map(dayString)
        case .enum:
            return value
        }
    }

    static func normalizeAll(_ texts: [String], type: FieldType) -> [String]? {
        var result: [String] = []
        for text in texts where !text.trimmingCharacters(in: .whitespaces).isEmpty {
            guard let value = normalize(text, type: type) else { return nil }
            result.append(value)
        }
        return result
    }

    static func day(from text: String) -> Date? {
        let head = String(text.prefix(10))
        let parts = head.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3, head.count == 10 else { return nil }
        var components = DateComponents()
        components.year = parts[0]
        components.month = parts[1]
        components.day = parts[2]
        components.hour = 12
        let calendar = Calendar(identifier: .gregorian)
        guard let date = calendar.date(from: components), calendar.dateComponents([.year, .month, .day], from: date) == DateComponents(year: parts[0], month: parts[1], day: parts[2]) else { return nil }
        return date
    }

    static func dayString(_ date: Date) -> String {
        let parts = Calendar(identifier: .gregorian).dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }

    static func display(_ field: TaskFieldValue, vocabulary: TaskListVocabulary, l: Localizer) -> [String] {
        switch field.type {
        case .enum:
            let options = vocabulary.enumValues(field.enumId)
            return field.values.enumerated().map { index, value in
                if let match = options.first(where: { $0.id.caseInsensitiveCompare(value) == .orderedSame }) { return match.name }
                return field.texts.indices.contains(index) ? field.texts[index] : value
            }
        case .bool:
            return field.values.map { $0 == "true" ? l.string(.taskDetailYes) : l.string(.taskDetailNo) }
        case .date:
            return field.values.map { day(from: $0).map(l.date) ?? $0 }
        case .int, .float, .string:
            return field.values
        }
    }
}

enum TaskReference {
    static let pattern = try! NSRegularExpression(pattern: "(?<![A-Za-z0-9_-])([A-Z][A-Z0-9]*)-([0-9]+)(?![A-Za-z0-9_-])")
    static let scheme = "tasker-task"

    static func url(_ ref: String) -> URL? {
        URL(string: "\(scheme):\(ref)")
    }

    static func ref(from url: URL) -> String? {
        guard url.scheme == scheme else { return nil }
        let text = url.absoluteString
        return String(text.dropFirst(scheme.count + 1))
    }

    static func matches(in text: String, prefixes: Set<String>) -> [(range: Range<String.Index>, ref: String)] {
        let whole = NSRange(text.startIndex..., in: text)
        return pattern.matches(in: text, range: whole).compactMap { match in
            guard let range = Range(match.range, in: text), let prefix = Range(match.range(at: 1), in: text) else { return nil }
            guard prefixes.contains(String(text[prefix])) else { return nil }
            return (range, String(text[range]))
        }
    }
}
