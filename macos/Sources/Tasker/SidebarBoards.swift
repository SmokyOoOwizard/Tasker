import SwiftUI
import TaskerAPI

struct SidebarBoardList: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(\.localizer) private var l
    @State private var boards: [BoardChoice] = []
    @State private var loaded: SidebarBoardsScope?

    private var scope: SidebarBoardsScope? {
        session.selection.current.map { SidebarBoardsScope(tab: session.id, project: $0.id) }
    }

    var body: some View {
        let shown = loaded == scope ? boards : []
        let active = session.section == .boards && session.boardEditor == nil ? session.currentBoard : nil
        VStack(alignment: .leading, spacing: 0) {
            ForEach(shown) { board in
                SidebarBoardRow(title: board.name, selected: board.id == active, dim: false) { open(board.id) }
            }
            if scope != nil {
                SidebarBoardRow(title: l.string(.boardTitleNew), selected: false, dim: true, plus: true, action: create)
            }
        }
        .padding(.leading, DesignLayout.sidebarSubIndent)
        .task(id: scope) { await follow() }
    }

    private func follow() async {
        guard let scope, let client = live.client else {
            loaded = nil
            boards = []
            return
        }
        await LiveQuery.follow(client, SidebarBoardsQuery(projectId: scope.project)) { data in
            boards = (data.project?.boards.data ?? []).map { BoardChoice(id: $0.id, name: $0.name) }
            loaded = scope
        }
    }

    private func open(_ id: UUID) {
        guard sessions.confirmDiscardingBoardEditor(in: session, replacing: nil, l) else { return }
        session.closeBoardEditor()
        session.showBoard(id)
    }

    private func create() {
        guard let project = session.selection.current?.id,
              sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: nil), l) else { return }
        session.openBoardEditor(project: project, board: nil, localizer: l)
    }
}

private struct SidebarBoardsScope: Hashable {
    var tab: UUID
    var project: UUID
}

private struct SidebarBoardRow: View {
    let title: String
    let selected: Bool
    let dim: Bool
    var plus = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: DesignSpacing.gap6) {
                if plus { MockupIconView(icon: .plus, size: 11) }
                Text(title)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            .font(.design(DesignTypography.sidebarSubSize))
            .foregroundStyle(selected ? DesignColor.tx.color : dim ? DesignColor.dim.color : DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignSpacing.sidebarSubPadY)
            .background(
                RoundedRectangle(cornerRadius: DesignRadius.control)
                    .fill(selected ? DesignColor.sel.color : hovering ? DesignColor.hov.color : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
