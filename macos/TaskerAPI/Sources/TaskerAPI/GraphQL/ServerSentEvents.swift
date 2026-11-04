import Foundation

struct ServerSentEvent: Sendable, Hashable {
    var event: String
    var data: String
}

struct ServerSentEventParser: Sendable {
    private var line: [UInt8] = []
    private var event = ""
    private var data: [String] = []
    private var hasData = false
    private var lastWasCarriageReturn = false

    mutating func consume(_ byte: UInt8) -> ServerSentEvent? {
        if byte == 0x0A, lastWasCarriageReturn {
            lastWasCarriageReturn = false
            return nil
        }
        lastWasCarriageReturn = byte == 0x0D
        if byte == 0x0A || byte == 0x0D {
            let text = String(decoding: line, as: UTF8.self)
            line.removeAll(keepingCapacity: true)
            return process(text)
        }
        line.append(byte)
        return nil
    }

    mutating func consume(_ bytes: some Sequence<UInt8>) -> [ServerSentEvent] {
        var events: [ServerSentEvent] = []
        for byte in bytes {
            if let event = consume(byte) { events.append(event) }
        }
        return events
    }

    private mutating func process(_ text: String) -> ServerSentEvent? {
        if text.isEmpty {
            defer {
                event = ""
                data = []
                hasData = false
            }
            guard hasData || !event.isEmpty else { return nil }
            return ServerSentEvent(event: event.isEmpty ? "message" : event, data: data.joined(separator: "\n"))
        }
        if text.hasPrefix(":") { return nil }
        let field: Substring
        var value: Substring
        if let colon = text.firstIndex(of: ":") {
            field = text[..<colon]
            value = text[text.index(after: colon)...]
            if value.hasPrefix(" ") { value = value.dropFirst() }
        } else {
            field = Substring(text)
            value = ""
        }
        switch field {
        case "event":
            event = String(value)
        case "data":
            data.append(String(value))
            hasData = true
        default:
            break
        }
        return nil
    }
}
