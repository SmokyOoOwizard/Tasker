import SwiftUI
import TaskerAPI

struct AdminSection: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l

    var body: some View {
        if let client = live.client {
            AdminHost(client: client)
        } else {
            LiveMessageView(text: l.string(.settingsLoading))
        }
    }
}

private struct AdminHost: View {
    let client: TaskerAPIClient
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l

    var body: some View {
        let model = session.attachAdmin(client: client, localizer: l)
        AdminLayout(model: model, client: client)
            .id(ObjectIdentifier(model))
    }
}

private struct AdminLayout: View {
    let model: AdminModel
    let client: TaskerAPIClient
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l

    var body: some View {
        switch model.phase {
        case .loading:
            LiveMessageView(text: l.string(.settingsLoading))
        case .failed(let message):
            LiveMessageView(text: l.string(.settingsLoadFailed, message))
        case .ready:
            HStack(spacing: 0) {
                nav
                DesignDivider(vertical: true)
                ScrollView {
                    page
                        .frame(maxWidth: 760, alignment: .leading)
                        .padding(.horizontal, DesignSpacing.pagePadX + 8)
                        .padding(.vertical, 22)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    private var nav: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(AdminPage.allCases) { page in
                AdminNavRow(page: page, selected: page == session.adminPage, badge: page == .problems ? model.problemCount : 0) {
                    session.adminPage = page
                }
            }
            Spacer(minLength: 0)
        }
        .padding(DesignSpacing.sidebarPadX)
        .frame(width: 200)
        .background(DesignColor.side.color)
    }

    @ViewBuilder
    private var page: some View {
        switch session.adminPage {
        case .profile: AdminProfilePage(model: model)
        case .agents: AdminAgentsPage(model: model)
        case .problems: AdminProblemsPage(model: model, client: client)
        }
    }
}

private struct AdminNavRow: View {
    let page: AdminPage
    let selected: Bool
    let badge: Int
    let action: () -> Void
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack {
                Text(l.string(page.titleKey))
                Spacer(minLength: 0)
                if badge > 0 { AdminCountBadge(count: badge) }
            }
            .foregroundStyle(selected ? DesignColor.tx.color : DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.rowPadX + 2)
            .padding(.vertical, DesignSpacing.rowPadY)
            .background(
                RoundedRectangle(cornerRadius: DesignRadius.control)
                    .fill(selected ? DesignColor.sel.color : hovering ? DesignColor.hov.color : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

struct AdminCountBadge: View {
    let count: Int
    @Environment(\.localizer) private var l

    var body: some View {
        Text(verbatim: count > 99 ? "99+" : String(count))
            .font(.design(DesignTypography.captionSize, weight: DesignTypography.semiboldWeight))
            .monospacedDigit()
            .foregroundStyle(DesignColor.onAcc.color)
            .padding(.horizontal, 6)
            .frame(minWidth: 18, minHeight: 16)
            .background(DesignColor.amber.color, in: Capsule())
            .accessibilityLabel(l.string(.adminProblemsBadge, String(count)))
    }
}

struct AdminSidebarBadge: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @State private var model: AdminModel?

    var body: some View {
        ZStack {
            Color.clear.frame(width: 0, height: 0)
            if let model, model.problemCount > 0 {
                AdminCountBadge(count: model.problemCount)
            }
        }
        .task(id: live.client.map(ObjectIdentifier.init)) {
            guard let client = live.client else { return }
            model = session.attachAdmin(client: client, localizer: l)
        }
        .onChange(of: live.revision) { model?.load() }
    }
}
