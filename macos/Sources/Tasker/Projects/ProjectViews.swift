import SwiftUI
import TaskerAPI

@MainActor
@Observable
final class ProjectSelection {
    private let tab: UUID
    private let layout: WorkspaceLayout
    private let live: LiveWorkspace

    init(tab: UUID, layout: WorkspaceLayout, live: LiveWorkspace) {
        self.tab = tab
        self.layout = layout
        self.live = live
    }

    var current: ProjectFields? {
        guard let projects = live.projects, let stored = layout.tab(tab) else { return nil }
        return ProjectChoice.resolve(projects, stored: stored.project)
    }

    func choose(_ id: UUID) {
        layout.setProject(id, tab: tab)
    }
}

struct ProjectSwitcher: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(\.localizer) private var l
    @State private var hovering = false
    @State private var creating = false

    var body: some View {
        let current = selection.current
        Menu {
            ForEach(live.projects ?? [], id: \.id) { project in
                Button {
                    selection.choose(project.id)
                } label: {
                    if project.id == current?.id {
                        Label(project.name, systemImage: "checkmark")
                    } else {
                        Text(project.name)
                    }
                }
            }
            if !(live.projects ?? []).isEmpty { Divider() }
            Button(l.string(.projectNew)) { creating = true }
                .disabled(live.client == nil)
        } label: {
            HStack(spacing: DesignSpacing.gap8) {
                Text(String((current?.name ?? l.string(.appLogoLetter)).prefix(1)).uppercased())
                    .font(.design(DesignTypography.logoSize, weight: DesignTypography.boldWeight))
                    .foregroundStyle(DesignColor.onAcc.color)
                    .frame(width: DesignLayout.logoSize, height: DesignLayout.logoSize)
                    .background(DesignColor.acc.color, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
                Text(current?.name ?? l.string(.projectNone))
                    .font(.designEmphasis(DesignTypography.baseSize))
                    .lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9))
                    .foregroundStyle(DesignColor.mut.color)
            }
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignLayout.searchPadY)
            .background(RoundedRectangle(cornerRadius: DesignRadius.control).fill(hovering ? DesignColor.hov.color : .clear))
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .onHover { hovering = $0 }
        .help(l.string(.sidebarProject))
        .padding(.top, DesignSpacing.gap6)
        .padding(.bottom, DesignSpacing.gap6)
        .sheet(isPresented: $creating) {
            NewProjectSheet()
        }
    }
}

struct NewProjectSheet: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(\.localizer) private var l
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var error: String?
    @State private var busy = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.projectNewTitle))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            TextField(l.string(.projectName), text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(submit)
            if let error {
                Text(error)
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.red.color)
            }
            HStack {
                Spacer()
                Button(l.string(.commonCancel)) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(l.string(.onboardingCreate), action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(busy)
            }
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: 360)
        .onAppear { focused = true }
    }

    private func submit() {
        guard let client = live.client, !busy else { return }
        switch ProjectNameCheck(name, existing: (live.projects ?? []).map(\.name)) {
        case .empty:
            error = l.string(.projectNameEmpty)
        case .duplicate:
            error = l.string(.projectExists)
        case .ok(let value):
            busy = true
            error = nil
            Task {
                do {
                    let id = try await ProjectService(client: client).create(name: value)
                    selection.choose(id)
                    dismiss()
                } catch {
                    self.error = l.string(.projectCreateFailed, error.localizedDescription)
                    busy = false
                }
            }
        }
    }
}
