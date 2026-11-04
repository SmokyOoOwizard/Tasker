import Foundation

public enum JSONValue: Sendable, Hashable, Codable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else {
            self = .object(try container.decode([String: JSONValue].self))
        }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .int(let value): try container.encode(value)
        case .double(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }

    public subscript(key: String) -> JSONValue? {
        if case .object(let object) = self { return object[key] }
        return nil
    }

    public var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }

    public var intValue: Int? {
        if case .int(let value) = self { return value }
        return nil
    }

    public var objectValue: [String: JSONValue]? {
        if case .object(let value) = self { return value }
        return nil
    }

    public var arrayValue: [JSONValue]? {
        if case .array(let value) = self { return value }
        return nil
    }
}

enum TaskerJSON {
    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            guard let date = TaskerDate.parse(text) else {
                throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid date: \(text)")
            }
            return date
        }
        return decoder
    }

    static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }

    static func decode<T: Decodable>(_ type: T.Type, from value: JSONValue) throws -> T {
        try decoder().decode(type, from: encoder().encode(value))
    }

    static func canonical(_ value: JSONValue) -> String {
        (try? String(decoding: encoder().encode(value), as: UTF8.self)) ?? ""
    }
}

public enum TaskerDate {
    public static func parse(_ text: String) -> Date? {
        let scalars = Array(text.utf8)
        var index = 0

        func number(_ digits: Int) -> Int? {
            guard index + digits <= scalars.count else { return nil }
            var result = 0
            for byte in scalars[index..<index + digits] {
                guard byte >= 48, byte <= 57 else { return nil }
                result = result * 10 + Int(byte - 48)
            }
            index += digits
            return result
        }

        func expect(_ characters: [UInt8]) -> Bool {
            guard index < scalars.count, characters.contains(scalars[index]) else { return false }
            index += 1
            return true
        }

        guard let year = number(4), expect([45]), let month = number(2), expect([45]), let day = number(2),
              expect([84, 116, 32]), let hour = number(2), expect([58]), let minute = number(2), expect([58]),
              let second = number(2) else { return nil }

        var fraction = 0.0
        if index < scalars.count, scalars[index] == 46 {
            index += 1
            var scale = 0.1
            let start = index
            while index < scalars.count, scalars[index] >= 48, scalars[index] <= 57 {
                fraction += Double(scalars[index] - 48) * scale
                scale /= 10
                index += 1
            }
            guard index > start else { return nil }
        }

        var offset = 0
        guard index < scalars.count else { return nil }
        if expect([90, 122]) {
            offset = 0
        } else if scalars[index] == 43 || scalars[index] == 45 {
            let sign = scalars[index] == 45 ? -1 : 1
            index += 1
            guard let hours = number(2) else { return nil }
            _ = expect([58])
            guard let minutes = number(2) else { return nil }
            offset = sign * (hours * 3600 + minutes * 60)
        } else {
            return nil
        }
        guard index == scalars.count else { return nil }

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let components = DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: second)
        guard let base = calendar.date(from: components) else { return nil }
        return base.addingTimeInterval(fraction - Double(offset))
    }

    public static func format(_ date: Date) -> String {
        date.formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    }
}
