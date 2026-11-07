import SwiftUI

struct SectionContentView: View {
    let section: AppSection
    @Environment(\.localizer) private var l

    var body: some View {
        if section == .tasks {
            TaskListSection()
        } else if section == .boards {
            ProjectScope {
                BoardsSection()
            }
        } else if section == .settings {
            SettingsSection()
        } else {
            AdminSection()
        }
    }
}

private struct BoardsSection: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(\.localizer) private var l

    var body: some View {
        if let editor = session.boardEditor {
            BoardEditorPage(model: editor, close: close)
                .id(ObjectIdentifier(editor))
                .onChange(of: live.revision) { editor.refreshLock() }
                .onChange(of: l.language, initial: true) { editor.localizer = l }
        } else if let client = live.client, let project = selection.current {
            BoardWorkArea(client: client, project: project, navigator: session.navigator)
                .id(project.id)
        }
    }

    private func close() {
        guard sessions.confirmDiscardingBoardEditor(in: session, l) else { return }
        session.closeBoardEditor()
    }
}
