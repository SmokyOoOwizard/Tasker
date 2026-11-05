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
        } else {
            placeholder
        }
    }

    private var placeholder: some View {
        VStack(spacing: 8) {
            MockupIconView(icon: section.icon, size: 36)
                .foregroundStyle(DesignColor.dim.color)
            Text(l.string(section.titleKey))
                .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
            Text(l.string(.placeholderHint))
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.mut.color)
            Text(l.string(.placeholderVersion, AppInfo.version))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct BoardsSection: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l

    var body: some View {
        if let editor = session.boardEditor {
            BoardEditorPage(model: editor, close: close)
                .id(ObjectIdentifier(editor))
                .onChange(of: live.revision) { editor.refreshLock() }
                .onChange(of: l.language, initial: true) { editor.localizer = l }
        } else {
            VStack(spacing: 8) {
                MockupIconView(icon: AppSection.boards.icon, size: 36)
                    .foregroundStyle(DesignColor.dim.color)
                Text(l.string(AppSection.boards.titleKey))
                    .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
                Text(l.string(.boardHint))
                    .font(.design(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.mut.color)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func close() {
        guard sessions.confirmDiscardingBoardEditor(in: session, l) else { return }
        session.closeBoardEditor()
    }
}
