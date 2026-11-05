import Foundation
import Observation

@MainActor
@Observable
final class TaskCreationCoordinator {
    var request: TaskCreationRequest?

    func present(parent: TaskLinkedBrief? = nil, series: UUID? = nil) {
        request = TaskCreationRequest(parent: parent, series: series)
    }

    func dismiss() {
        request = nil
    }
}
