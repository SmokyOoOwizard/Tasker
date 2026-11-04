import Foundation
import Observation

@MainActor
@Observable
final class TaskNavigator {
    private(set) var peek: UUID?
    private(set) var card: UUID?
    private(set) var history: [UUID] = []

    var canGoBack: Bool { !history.isEmpty }

    func togglePeek(_ id: UUID) {
        peek = peek == id ? nil : id
    }

    func showPeek(_ id: UUID) {
        peek = id
    }

    func closePeek() {
        peek = nil
    }

    func open(_ id: UUID) {
        if let card, card != id { history.append(card) }
        card = id
        peek = nil
    }

    func back() {
        if let previous = history.popLast() {
            card = previous
        } else {
            card = nil
        }
    }

    func closeCard() {
        card = nil
        history = []
    }

    func reset() {
        peek = nil
        card = nil
        history = []
    }
}
