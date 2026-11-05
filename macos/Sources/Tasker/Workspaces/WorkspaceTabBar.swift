import SwiftUI

struct WorkspaceTabBar: View {
    let window: WorkspaceWindow
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(DaemonConnection.self) private var connection
    static let gap: CGFloat = 96
    static let space = "workspaceTabBar"

    private var insertion: Int? {
        let target = workspaces.dropZones.target
        return target?.window == window.id ? target?.position : nil
    }

    var body: some View {
        let insertion = insertion
        HStack(alignment: .bottom, spacing: DesignSpacing.gap2) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(alignment: .bottom, spacing: DesignSpacing.gap2) {
                    ForEach(Array(window.tabs.enumerated()), id: \.element.id) { index, tab in
                        WorkspaceTabItem(
                            tab: tab,
                            window: window,
                            title: title(tab),
                            selected: window.selected == tab.id,
                            status: WorkspaceTabStatus(path: tab.path, in: connection.state.status)
                        )
                        .padding(.leading, insertion == index ? Self.gap : 0)
                        .background(GeometryReader { proxy in
                            Color.clear.preference(key: WorkspaceTabFrames.self, value: [tab.id: proxy.frame(in: .named(Self.space))])
                        })
                    }
                    if insertion == window.tabs.count {
                        Color.clear.frame(width: Self.gap, height: 1)
                    }
                }
                .animation(.easeInOut(duration: 0.15), value: insertion)
                .animation(.easeInOut(duration: 0.15), value: window.tabs.map(\.id))
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
        .coordinateSpace(name: Self.space)
        .background(WorkspaceTabBarAnchor(window: window.id, zones: workspaces.dropZones))
        .onPreferenceChange(WorkspaceTabFrames.self) { workspaces.dropZones.setFrames($0, window: window.id) }
    }

    private func title(_ tab: WorkspaceTab) -> String {
        let name = WorkspacePath.name(tab.path)
        guard window.tabs.filter({ $0.path == tab.path }).count > 1,
              let project = sessions.session(for: tab).selection.current?.name else { return name }
        return name + " · " + project
    }
}

private struct WorkspaceTabLabel: View {
    let title: String
    let selected: Bool
    let hovering: Bool
    let status: WorkspaceTabStatus
    let close: () -> Void
    let closeHelp: String

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "folder")
                .font(.system(size: 11))
            Text(verbatim: title)
                .lineLimit(1)
            indicator
            Button(action: close) {
                Image(systemName: "xmark")
                    .font(.system(size: 8, weight: .semibold))
                    .frame(width: 14, height: 14)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .opacity(hovering || selected ? 1 : 0)
            .help(closeHelp)
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
}

private struct WorkspaceTabItem: View {
    let tab: WorkspaceTab
    let window: WorkspaceWindow
    let title: String
    let selected: Bool
    let status: WorkspaceTabStatus
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(\.localizer) private var l
    @Environment(\.colorScheme) private var scheme
    @Environment(\.displayScale) private var displayScale
    @Environment(\.openWindow) private var openWindow
    @State private var hovering = false

    private var layout: WorkspaceLayout { workspaces.layout }

    private func label(hovering: Bool, selected: Bool) -> WorkspaceTabLabel {
        WorkspaceTabLabel(
            title: title,
            selected: selected,
            hovering: hovering,
            status: status,
            close: { layout.close(tab.id) },
            closeHelp: l.string(.workspaceCloseTab)
        )
    }

    var body: some View {
        label(hovering: hovering, selected: selected)
            .overlay {
                WorkspaceTabDragSurface(
                    reservedTrailing: 26,
                    image: dragImage,
                    click: { layout.select(tab.id) },
                    begin: { layout.beginDragging(tab.id) },
                    moved: { point in
                        workspaces.dropZones.update(screenPoint: point, layout: layout, gap: WorkspaceTabBar.gap)
                        return workspaces.dropZones.target != nil
                    },
                    end: finishDrag
                )
            }
            .opacity(layout.draggingTab == tab.id ? 0.4 : 1)
            .padding(.bottom, selected ? -1 : 0)
            .contentShape(Rectangle())
            .onHover { hovering = $0 }
            .contextMenu { WorkspaceTabMenu(tab: tab.id, window: window) }
            .help(helpText)
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(selected ? .isSelected : [])
            .accessibilityAction { layout.select(tab.id) }
    }

    @MainActor
    private func dragImage() -> NSImage? {
        let renderer = ImageRenderer(content: label(hovering: false, selected: true).environment(\.colorScheme, scheme))
        renderer.scale = displayScale
        return renderer.nsImage
    }

    private func finishDrag(source: NSWindow?, point: NSPoint, completed: Bool) {
        let target = workspaces.dropZones.target
        workspaces.dropZones.clear()
        layout.endDragging()
        guard completed else { return }
        if let target {
            layout.move(tab.id, to: target.window, at: target.position)
            return
        }
        guard let source, !source.frame.contains(point) else { return }
        let topLeft = NSPoint(x: point.x - 120, y: point.y + 16)
        guard let current = layout.window(containing: tab.id), current.tabs.count > 1 else {
            source.setFrameTopLeftPoint(topLeft)
            return
        }
        let size = source.frame.size
        let frame = NSRect(x: topLeft.x, y: topLeft.y - size.height, width: size.width, height: size.height)
        if let id = layout.detach(tab.id, frame: frame) {
            openWindow(value: id)
        }
    }

    private var helpText: String {
        switch status {
        case .failed(let message): l.string(.workspaceFailed, message)
        case .absent: l.string(.workspaceAbsent)
        case .opening: l.string(.workspaceOpening)
        case .open: tab.path
        }
    }
}

private struct WorkspaceTabMenu: View {
    let tab: UUID
    let window: WorkspaceWindow
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(\.localizer) private var l
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let layout = workspaces.layout
        Button(l.string(.tabDuplicate)) { layout.duplicate(tab) }
        Divider()
        Button(l.string(.tabMoveToNewWindow)) {
            if let id = layout.detach(tab) { openWindow(value: id) }
        }
        .disabled(window.tabs.count < 2)
        let others = layout.windows.filter { $0.id != window.id }
        if !others.isEmpty {
            Menu(l.string(.tabMoveToWindow)) {
                ForEach(others) { other in
                    Button(other.title(fallback: l.string(.appName))) { layout.move(tab, to: other.id) }
                }
            }
        }
        Divider()
        Button(l.string(.workspaceCloseTab)) { layout.close(tab) }
    }
}

private struct OpenFolderButton: View {
    @Environment(WorkspaceController.self) private var workspaces
    @Environment(\.workspaceWindow) private var window
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        let recents = workspaces.reopenable(in: connection.state.status)
        let open = workspaces.layout.openPaths
        Group {
            if recents.isEmpty && open.isEmpty {
                Button(action: openFolder) { label }
            } else {
                Menu {
                    Button(l.string(.workspaceOpen), action: openFolder)
                    if !open.isEmpty {
                        Divider()
                        Section(l.string(.workspaceOpenTabs)) {
                            ForEach(open, id: \.self) { path in
                                Button(WorkspacePath.name(path)) {
                                    workspaces.layout.newTab(path, in: window)
                                }
                                .help(path)
                            }
                        }
                    }
                    if !recents.isEmpty {
                        Divider()
                        Section(l.string(.workspaceRecent)) {
                            ForEach(recents, id: \.self) { path in
                                Button(WorkspacePath.name(path)) {
                                    Task { await workspaces.reopen(path, window: window) }
                                }
                                .help(path)
                            }
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
        Task { await workspaces.openFolder(l, window: window) }
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
