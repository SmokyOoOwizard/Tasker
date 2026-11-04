import SwiftUI

struct WorkspaceTabBar: View {
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l

    var body: some View {
        let tabs = workspaces.tabs
        HStack(alignment: .bottom, spacing: DesignSpacing.gap2) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(alignment: .bottom, spacing: DesignSpacing.gap2) {
                    ForEach(tabs.open, id: \.self) { path in
                        WorkspaceTab(path: path, selected: tabs.selected == path, status: WorkspaceTabStatus(path: path, in: connection.state.status))
                    }
                }
            }
            .fixedSize(horizontal: true, vertical: false)
            OpenFolderButton()
            Spacer(minLength: 0)
        }
        .padding(.leading, 80)
        .padding(.trailing, 12)
        .frame(height: DesignLayout.tabBarHeight)
        .background(DesignColor.side.color)
        .overlay(alignment: .bottom) {
            DesignDivider()
        }
    }
}

private struct WorkspaceTab: View {
    let path: String
    let selected: Bool
    let status: WorkspaceTabStatus
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "folder")
                .font(.system(size: 11))
            Text(WorkspacePath.name(path))
                .lineLimit(1)
            indicator
            Button {
                workspaces.tabs.close(path)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 8, weight: .semibold))
                    .frame(width: 14, height: 14)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .opacity(hovering || selected ? 1 : 0)
            .help(l.string(.workspaceCloseTab))
        }
        .font(.design(DesignTypography.smallSize))
        .foregroundStyle(selected ? DesignColor.tx.color : DesignColor.mut.color)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(
            UnevenRoundedRectangle(topLeadingRadius: DesignRadius.control, topTrailingRadius: DesignRadius.control)
                .fill(selected ? DesignColor.bg.color : hovering ? DesignColor.hov.color : .clear)
        )
        .overlay(
            UnevenRoundedRectangle(topLeadingRadius: DesignRadius.control, topTrailingRadius: DesignRadius.control)
                .strokeBorder(selected ? DesignColor.line.color : .clear, lineWidth: DesignLayout.borderWidth)
        )
        .padding(.bottom, selected ? -1 : 0)
        .contentShape(Rectangle())
        .onTapGesture { workspaces.tabs.select(path) }
        .onHover { hovering = $0 }
        .help(helpText)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    @ViewBuilder
    private var indicator: some View {
        switch status {
        case .open:
            EmptyView()
        case .opening:
            Circle().fill(DesignColor.amber.color).frame(width: 6, height: 6)
        case .failed, .absent:
            Circle().fill(DesignColor.red.color).frame(width: 6, height: 6)
        }
    }

    private var helpText: String {
        switch status {
        case .failed(let message): l.string(.workspaceFailed, message)
        case .absent: l.string(.workspaceAbsent)
        case .opening: l.string(.workspaceOpening)
        case .open: path
        }
    }
}

private struct OpenFolderButton: View {
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        let recents = workspaces.reopenable(in: connection.state.status)
        Group {
            if recents.isEmpty {
                Button(action: openFolder) { label }
            } else {
                Menu {
                    Button(l.string(.workspaceOpen), action: openFolder)
                    Divider()
                    Section(l.string(.workspaceRecent)) {
                        ForEach(recents, id: \.self) { path in
                            Button(WorkspacePath.name(path)) {
                                Task { await workspaces.reopen(path) }
                            }
                            .help(path)
                        }
                    }
                } label: {
                    label
                }
                .menuStyle(.button)
                .menuIndicator(.hidden)
            }
        }
        .buttonStyle(.plain)
        .fixedSize()
        .onHover { hovering = $0 }
    }

    private var label: some View {
        HStack(spacing: 6) {
            Image(systemName: "plus")
                .font(.system(size: 10, weight: .semibold))
            Text(l.string(.workspaceOpen))
        }
        .font(.design(DesignTypography.smallSize))
        .foregroundStyle(DesignColor.mut.color)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(UnevenRoundedRectangle(topLeadingRadius: DesignRadius.control, topTrailingRadius: DesignRadius.control).fill(hovering ? DesignColor.hov.color : .clear))
        .contentShape(Rectangle())
    }

    private func openFolder() {
        Task { await workspaces.openFolder(l) }
    }
}

struct WorkspaceFailureBanner: View {
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(\.localizer) private var l

    var body: some View {
        if let failure = workspaces.failure {
            HStack(spacing: DesignSpacing.gap8) {
                Text(l.string(.workspaceAddFailed, failure))
                    .lineLimit(2)
                Spacer(minLength: 0)
                Button {
                    workspaces.dismissFailure()
                } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .semibold))
                }
                .buttonStyle(.plain)
                .help(l.string(.workspaceCloseTab))
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.red.color)
            .padding(.horizontal, DesignSpacing.pagePadX)
            .padding(.vertical, DesignSpacing.gap6)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(DesignColor.accbg.color)
        }
    }
}
