import Foundation

enum MarkdownBlock: Hashable, Sendable {
    case heading(level: Int, text: String)
    case paragraph(String)
    case listItem(marker: String, level: Int, text: String)
    case quote(String)
    case code(String)
    case rule
    case table(header: [String], alignments: [MarkdownColumnAlignment], rows: [[String]])
}

enum MarkdownColumnAlignment: Hashable, Sendable {
    case leading
    case center
    case trailing
}

enum MarkdownParser {
    static func parse(_ source: String) -> [MarkdownBlock] {
        let lines = source.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var blocks: [MarkdownBlock] = []
        var paragraph: [String] = []
        var quote: [String] = []
        var index = 0

        func flushParagraph() {
            if !paragraph.isEmpty { blocks.append(.paragraph(paragraph.joined(separator: "\n"))) }
            paragraph = []
        }
        func flushQuote() {
            if !quote.isEmpty { blocks.append(.quote(quote.joined(separator: "\n"))) }
            quote = []
        }

        while index < lines.count {
            let line = lines[index]
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") {
                flushParagraph()
                flushQuote()
                let fence = String(trimmed.prefix(3))
                var code: [String] = []
                index += 1
                while index < lines.count, !lines[index].trimmingCharacters(in: .whitespaces).hasPrefix(fence) {
                    code.append(lines[index])
                    index += 1
                }
                blocks.append(.code(code.joined(separator: "\n")))
                index += 1
                continue
            }
            if trimmed.isEmpty {
                flushParagraph()
                flushQuote()
            } else if index + 1 < lines.count, let table = table(header: trimmed, delimiter: lines[index + 1].trimmingCharacters(in: .whitespaces), lines: lines, from: index + 2) {
                flushParagraph()
                flushQuote()
                blocks.append(table.block)
                index = table.next
                continue
            } else if let heading = heading(trimmed) {
                flushParagraph()
                flushQuote()
                blocks.append(heading)
            } else if isRule(trimmed) {
                flushParagraph()
                flushQuote()
                blocks.append(.rule)
            } else if trimmed.hasPrefix(">") {
                flushParagraph()
                quote.append(String(trimmed.dropFirst()).trimmingCharacters(in: .whitespaces))
            } else if let item = listItem(line) {
                flushParagraph()
                flushQuote()
                blocks.append(item)
            } else {
                flushQuote()
                paragraph.append(trimmed)
            }
            index += 1
        }
        flushParagraph()
        flushQuote()
        return blocks
    }

    private static func splitRow(_ line: String) -> [String] {
        var text = line.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("|") { text.removeFirst() }
        if text.hasSuffix("|"), !text.hasSuffix("\\|") { text.removeLast() }
        var cells: [String] = []
        var current = ""
        var escaped = false
        for character in text {
            if escaped {
                if character != "|" { current.append("\\") }
                current.append(character)
                escaped = false
            } else if character == "\\" {
                escaped = true
            } else if character == "|" {
                cells.append(current.trimmingCharacters(in: .whitespaces))
                current = ""
            } else {
                current.append(character)
            }
        }
        if escaped { current.append("\\") }
        cells.append(current.trimmingCharacters(in: .whitespaces))
        return cells
    }

    private static func table(header: String, delimiter: String, lines: [String], from start: Int) -> (block: MarkdownBlock, next: Int)? {
        guard header.contains("|"), delimiter.contains("-") else { return nil }
        let delimiterCells = splitRow(delimiter)
        var alignments: [MarkdownColumnAlignment] = []
        for cell in delimiterCells {
            let core = cell.trimmingCharacters(in: CharacterSet(charactersIn: ":"))
            guard !core.isEmpty, core.allSatisfy({ $0 == "-" }), cell.filter({ $0 == ":" }).count <= 2 else { return nil }
            let left = cell.hasPrefix(":"), right = cell.hasSuffix(":")
            alignments.append(left && right ? .center : (right ? .trailing : .leading))
        }
        let headerCells = splitRow(header)
        guard headerCells.count == alignments.count else { return nil }
        var rows: [[String]] = []
        var index = start
        while index < lines.count {
            let row = lines[index].trimmingCharacters(in: .whitespaces)
            if row.isEmpty || !row.contains("|") { break }
            var cells = splitRow(row)
            if cells.count > alignments.count { cells = Array(cells.prefix(alignments.count)) }
            while cells.count < alignments.count { cells.append("") }
            rows.append(cells)
            index += 1
        }
        return (.table(header: headerCells, alignments: alignments, rows: rows), index)
    }

    private static func heading(_ line: String) -> MarkdownBlock? {
        let hashes = line.prefix { $0 == "#" }.count
        guard (1...6).contains(hashes), line.dropFirst(hashes).first == " " else { return nil }
        return .heading(level: hashes, text: line.dropFirst(hashes).trimmingCharacters(in: .whitespaces))
    }

    private static func isRule(_ line: String) -> Bool {
        let stripped = line.replacingOccurrences(of: " ", with: "")
        guard stripped.count >= 3, let first = stripped.first, "-*_".contains(first) else { return false }
        return stripped.allSatisfy { $0 == first }
    }

    private static func listItem(_ line: String) -> MarkdownBlock? {
        var spaces = 0
        for character in line {
            if character == " " { spaces += 1 } else if character == "\t" { spaces += 4 } else { break }
        }
        let rest = line.trimmingCharacters(in: .whitespaces)
        let level = min(spaces / 2, 4)
        for bullet in ["- ", "* ", "+ "] where rest.hasPrefix(bullet) {
            var text = String(rest.dropFirst(2))
            if text.hasPrefix("[ ] ") { return .listItem(marker: "☐", level: level, text: String(text.dropFirst(4))) }
            if text.lowercased().hasPrefix("[x] ") { return .listItem(marker: "☑", level: level, text: String(text.dropFirst(4))) }
            text = text.trimmingCharacters(in: .whitespaces)
            return .listItem(marker: "•", level: level, text: text)
        }
        let digits = rest.prefix { $0.isNumber }
        if !digits.isEmpty, digits.count <= 9 {
            let tail = rest.dropFirst(digits.count)
            if tail.hasPrefix(". ") || tail.hasPrefix(") ") {
                return .listItem(marker: "\(digits).", level: level, text: tail.dropFirst(2).trimmingCharacters(in: .whitespaces))
            }
        }
        return nil
    }

    static func inline(_ text: String, prefixes: Set<String>) -> AttributedString {
        var result = (try? AttributedString(
            markdown: text,
            options: AttributedString.MarkdownParsingOptions(allowsExtendedAttributes: false, interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
        let plain = String(result.characters)
        for match in TaskReference.matches(in: plain, prefixes: prefixes) {
            let start = result.index(result.startIndex, offsetByCharacters: plain.distance(from: plain.startIndex, to: match.range.lowerBound))
            let end = result.index(start, offsetByCharacters: plain.distance(from: match.range.lowerBound, to: match.range.upperBound))
            guard result[start..<end].link == nil, result[start..<end].runs.allSatisfy({ $0.inlinePresentationIntent?.contains(.code) != true }), let url = TaskReference.url(match.ref) else { continue }
            result[start..<end].link = url
        }
        return result
    }
}
