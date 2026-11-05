import Foundation

struct SeriesNumberConflict: Hashable, Sendable {
    var seriesId: UUID
    var number: Int
    var taskIds: [UUID]
}

struct SeriesInfo: Hashable, Sendable {
    var id: UUID
    var name: String
    var prefix: String
    var version: String
}

struct SeriesBrief: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var prefix: String
}

struct SeriesOverview: Hashable, Sendable {
    var info: SeriesInfo
    var all: [SeriesBrief]
    var conflicts: [SeriesNumberConflict]
}

struct SeriesStats: Hashable, Sendable {
    var count: Int
    var maxNumber: Int
}

struct SeriesPlanStep: Hashable, Sendable, Identifiable {
    var taskId: UUID
    var title: String
    var from: Int
    var to: Int
    var keeperId: UUID
    var keeperTitle: String

    var id: UUID { taskId }
}

struct SeriesConflictGroup: Hashable, Sendable, Identifiable {
    var number: Int
    var keeperTitle: String
    var moves: [SeriesPlanStep]

    var id: Int { number }
}

struct SeriesConflictPlan: Hashable, Sendable {
    var token: String
    var steps: [SeriesPlanStep]

    var isEmpty: Bool { steps.isEmpty }

    var groups: [SeriesConflictGroup] {
        var order: [Int] = []
        var byNumber: [Int: SeriesConflictGroup] = [:]
        for step in steps {
            if byNumber[step.from] == nil {
                order.append(step.from)
                byNumber[step.from] = SeriesConflictGroup(number: step.from, keeperTitle: step.keeperTitle, moves: [])
            }
            byNumber[step.from]?.moves.append(step)
        }
        return order.compactMap { byNumber[$0] }
    }
}

enum SeriesFormError: Equatable, Sendable {
    case nameRequired
    case prefixInvalid
    case prefixTaken(String)
}

enum SeriesForm {
    static let maxPrefixLength = 20

    static func validate(name: String, prefix: String, others: [String]) -> SeriesFormError? {
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return .nameRequired }
        guard !prefix.isEmpty, prefix.count <= maxPrefixLength, prefix.allSatisfy(isPrefixCharacter) else { return .prefixInvalid }
        if others.contains(prefix) { return .prefixTaken(prefix) }
        return nil
    }

    private static func isPrefixCharacter(_ character: Character) -> Bool {
        character.isASCII && (character.isLetter || character.isNumber)
    }
}
