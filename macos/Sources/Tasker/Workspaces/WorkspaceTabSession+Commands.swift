import AppKit
import Foundation

enum TaskRequest: Equatable {
    case link
    case parent
}

struct TaskStatusChoices {
    var reference: String
    var current: UUID?
    var options: [TaskListVocabulary.Status]
    var apply: @MainActor (UUID) -> Void
}

extension WorkspaceTabSession {
    var commandTask: UUID? {
        switch section {
        case .tasks: return navigator.card ?? navigator.peek ?? listSelection
        case .boards: return navigator.card ?? navigator.peek
        default: return nil
        }
    }

    var visibleDetail: TaskDetailModel? {
        if navigator.card != nil { return cardModel }
        return navigator.peek != nil ? peekModel : nil
    }

    var currentListMode: TaskListMode? {
        guard let project = selection.current?.id else { return nil }
        return settings.preferences(for: project).mode
    }

    func registerDetail(_ model: TaskDetailModel, compact: Bool) {
        if compact { peekModel = model } else { cardModel = model }
    }

    func setListMode(_ mode: TaskListMode) {
        guard let project = selection.current?.id else { return }
        settings.update(project) { $0.mode = mode }
    }

    func taskReference() -> String? {
        guard let task = commandTask else { return nil }
        if let detail = visibleDetail, detail.taskId == task, let ready = detail.readyReference { return ready }
        guard let listModel, let row = listModel.nodes(task: task).first?.row else { return nil }
        return listModel.vocabulary.reference(row)
    }

    func statusChoices() -> TaskStatusChoices? {
        guard let task = commandTask else { return nil }
        if let detail = visibleDetail, detail.taskId == task, let current = detail.detail {
            return TaskStatusChoices(
                reference: detail.reference, current: current.statusId, options: detail.statusOptions,
                apply: { [weak detail] status in detail?.perform(.status(status)) })
        }
        guard let listModel, let node = listModel.nodes(task: task).first, let row = node.row else { return nil }
        return TaskStatusChoices(
            reference: listModel.vocabulary.reference(row), current: row.statusId, options: listModel.statusOptions(node),
            apply: { [weak listModel] status in listModel?.changeStatus(node, to: status) })
    }

    func requestLink(_ request: TaskRequest) {
        guard let task = commandTask else { return }
        if navigator.card != task { navigator.open(task) }
        taskRequest = request
    }

    func copyTaskKey(_ l: Localizer) {
        guard let reference = taskReference() else { return }
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(reference, forType: .string)
        notice(l.string(.taskKeyCopied, reference))
    }

    func currentLink() -> TaskerLink? {
        guard let path else { return nil }
        if let task = commandTask {
            if let reference = taskReference(), TaskerLink.isReference(reference) {
                return TaskerLink(path: path, target: .task(reference: reference))
            }
            return TaskerLink(path: path, target: .taskID(task))
        }
        if section == .tasks, seriesPage != nil, let prefix = seriesPrefix, TaskerLink.isPrefix(prefix) {
            return TaskerLink(path: path, target: .series(prefix: prefix))
        }
        if section == .boards, let board = boardEditor?.target.board {
            return TaskerLink(path: path, target: .board(board))
        }
        return nil
    }

    func copyLink(_ l: Localizer) {
        guard let link = currentLink() else { return }
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(link.string, forType: .string)
        notice(l.string(.deeplinkCopied))
    }
}
