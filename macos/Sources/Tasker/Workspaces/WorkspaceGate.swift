import SwiftUI

struct WorkspaceGate<Content: View>: View {
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l
    @ViewBuilder var content: () -> Content

    var body: some View {
        if let path = workspaces.tabs.selected {
            switch WorkspaceTabStatus(path: path, in: connection.state.status) {
            case .open:
                content()
                    .id(path)
            case .opening:
                message(l.string(.workspaceOpening))
            case .failed(let error):
                message(l.string(.workspaceFailed, error))
            case .absent:
                message(l.string(.workspaceAbsent)) {
                    Button(l.string(.workspaceConnect)) {
                        Task { await workspaces.reconnect(path) }
                    }
                }
            }
        } else {
            message(l.string(.workspaceNoneOpen)) {
                Button(l.string(.workspaceOpen)) {
                    Task { await workspaces.openFolder(l) }
                }
            }
        }
    }

    private func message(_ text: String) -> some View {
        message(text) { EmptyView() }
    }

    private func message<Actions: View>(_ text: String, @ViewBuilder actions: () -> Actions) -> some View {
        VStack(spacing: DesignSpacing.gap10) {
            Text(text)
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.mut.color)
            actions()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
