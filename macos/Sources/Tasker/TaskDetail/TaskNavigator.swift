import Foundation
import Observation

@MainActor
@Observable
final class TaskNavigator {
    private(set) var peek: UUID? { didSet { onChange?() } }
    private(set) var card: UUID? { didSet { onChange?() } }
    private(set) var history: [UUID] = [] { didSet { onChange?() } }
    private(set) var labels: [UUID: String] = [:]

    @ObservationIgnored var onChange: (() -> Void)?

    var canGoBack: Bool { !history.isEmpty }

    func restore(peek: UUID?, card: UUID?, history: [UUID]) {
        self.peek = peek
        self.card = card
        self.history = card == nil ? [] : history
    }

    func togglePeek(_ id: UUID) {
        peek = peek == id ? nil : id
    }

    func showPeek(_ id: UUID) {
        peek = id
    }

    func label(_ id: UUID, _ text: String) {
        if labels[id] != text { labels[id] = text }
    }

    func jump(toHistory index: Int) {
        guard history.indices.contains(index) else { return }
        card = history[index]
        history = Array(history.prefix(index))
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
