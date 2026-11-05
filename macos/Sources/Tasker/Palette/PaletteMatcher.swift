import Foundation

enum PaletteMatcher {
    static func normalize(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
    }

    static func score(_ query: String, in text: String) -> Int {
        let tokens = normalize(query).split(whereSeparator: \.isWhitespace).map(String.init)
        guard !tokens.isEmpty else { return 1 }
        let haystack = normalize(text)
        var total = 0
        for token in tokens {
            let value = tokenScore(token, in: haystack)
            if value == 0 { return 0 }
            total += value
        }
        return total / tokens.count
    }

    private static func tokenScore(_ token: String, in haystack: String) -> Int {
        if let range = haystack.range(of: token) {
            let position = haystack.distance(from: haystack.startIndex, to: range.lowerBound)
            let wordStart = range.lowerBound == haystack.startIndex || !haystack[haystack.index(before: range.lowerBound)].isLetter && !haystack[haystack.index(before: range.lowerBound)].isNumber
            return 100 - min(position, 60) + (wordStart ? 40 : 0)
        }
        var rest = haystack[...]
        for character in token {
            guard let found = rest.firstIndex(of: character) else { return 0 }
            rest = rest[rest.index(after: found)...]
        }
        return 10
    }

    static func ranked(_ entries: [PaletteEntry], query: String) -> [PaletteEntry] {
        guard !query.trimmingCharacters(in: .whitespaces).isEmpty else { return entries }
        var scored: [(index: Int, score: Int)] = []
        for (index, entry) in entries.enumerated() {
            let text = entry.title + " " + (entry.reference ?? "") + " " + entry.keywords
            let value = score(query, in: text)
            if value > 0 { scored.append((index, value)) }
        }
        scored.sort { $0.score == $1.score ? $0.index < $1.index : $0.score > $1.score }
        return scored.map { entries[$0.index] }
    }

    static func looksLikeReference(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        guard let dash = trimmed.firstIndex(of: "-") else { return false }
        let prefix = trimmed[..<dash]
        let number = trimmed[trimmed.index(after: dash)...]
        return !prefix.isEmpty && prefix.allSatisfy { $0.isLetter || $0.isNumber } && !number.isEmpty && number.allSatisfy(\.isNumber)
    }
}
