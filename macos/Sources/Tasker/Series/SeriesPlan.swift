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

struct SeriesConflictTask: Hashable, Sendable {
    var id: UUID
    var title: String
    var createdAt: Date
    var version: String
    var numbers: [TaskSeriesRef]
}

struct SeriesRenumberStep: Hashable, Sendable, Identifiable {
    var task: SeriesConflictTask
    var from: Int
    var to: Int

    var id: UUID { task.id }
}

struct SeriesConflictGroup: Hashable, Sendable, Identifiable {
    var number: Int
    var keeper: SeriesConflictTask
    var moves: [SeriesRenumberStep]

    var id: Int { number }
}

struct SeriesConflictPlan: Hashable, Sendable {
    var groups: [SeriesConflictGroup]

    var steps: [SeriesRenumberStep] { groups.flatMap(\.moves) }
    var isEmpty: Bool { groups.isEmpty }
}

enum SeriesConflictPlanner {
    static func plan(conflicts: [(number: Int, tasks: [SeriesConflictTask])], maxNumber: Int) -> SeriesConflictPlan {
        var next = maxNumber
        var groups: [SeriesConflictGroup] = []
        for conflict in conflicts.sorted(by: { $0.number < $1.number }) {
            let ordered = conflict.tasks.sorted {
                $0.createdAt == $1.createdAt ? $0.id.uuidString < $1.id.uuidString : $0.createdAt < $1.createdAt
            }
            guard let keeper = ordered.first, ordered.count > 1 else { continue }
            var moves: [SeriesRenumberStep] = []
            for task in ordered.dropFirst() {
                next += 1
                moves.append(SeriesRenumberStep(task: task, from: conflict.number, to: next))
            }
            groups.append(SeriesConflictGroup(number: conflict.number, keeper: keeper, moves: moves))
        }
        return SeriesConflictPlan(groups: groups)
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
