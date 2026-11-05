import SwiftUI
import TaskerAPI

struct SettingsSection: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(\.localizer) private var l

    var body: some View {
        if let client = live.client, let project = selection.current {
            SettingsHost(client: client, project: project)
                .id(project.id)
        } else {
            ProjectScope { EmptyView() }
        }
    }
}

private struct SettingsHost: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l

    var body: some View {
        let model = session.attachSettings(project: project.id, client: client, localizer: l)
        SettingsLayout(model: model)
            .id(ObjectIdentifier(model))
            .onChange(of: live.revision) { model.load() }
            .onChange(of: l.language, initial: true) { model.localizer = l }
    }
}

private struct SettingsLayout: View {
    let model: SettingsModel
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

    private var current: SettingsPage {
        model.pages.contains(session.settingsPage) ? session.settingsPage : (model.pages.first ?? .series)
    }

    private var nav: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(model.pages) { page in
                SettingsNavRow(page: page, selected: page == current) { session.settingsPage = page }
            }
            Spacer(minLength: 0)
        }
        .padding(DesignSpacing.sidebarPadX)
        .frame(width: 200)
        .background(DesignColor.side.color)
    }

    @ViewBuilder
    private var page: some View {
        switch current {
        case .series: SeriesSettingsPage(model: model)
        case .linkTypes: LinkTypesSettingsPage(model: model)
        case .fields: FieldsSettingsPage(model: model)
        case .enums: EnumsSettingsPage(model: model)
        case .members: MembersSettingsPage(model: model)
        case .project: ProjectSettingsPage(model: model)
        }
    }
}

private struct SettingsNavRow: View {
    let page: SettingsPage
    let selected: Bool
    let action: () -> Void
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(l.string(page.titleKey))
                .frame(maxWidth: .infinity, alignment: .leading)
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

struct SettingsPageFrame<Content: View>: View {
    let title: L10nKey
    var lead: String?
    var add: (title: String, action: () -> Void)?
    @ViewBuilder var content: () -> Content
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(title))
                .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
            if let lead {
                Text(lead)
                    .foregroundStyle(DesignColor.mut.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let add {
                Button {
                    add.action()
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .plus, size: 12)
                        Text(add.title)
                    }
                }
                .padding(.top, 2)
            }
            content()
                .padding(.top, DesignSpacing.gap6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct SettingsRow<Title: View, Meta: View, Actions: View>: View {
    @ViewBuilder var title: () -> Title
    @ViewBuilder var meta: () -> Meta
    @ViewBuilder var actions: () -> Actions

    var body: some View {
        HStack(alignment: .top, spacing: DesignSpacing.gap10) {
            VStack(alignment: .leading, spacing: 3) {
                title().font(.designEmphasis(DesignTypography.baseSize))
                meta()
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
            }
            Spacer(minLength: 0)
            HStack(spacing: 2) { actions() }
        }
        .padding(.vertical, 9)
        .overlay(alignment: .bottom) { DesignDivider() }
    }
}

struct SettingsIconButton: View {
    let icon: MockupIcon
    let help: String
    var destructive = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            MockupIconView(icon: icon, size: 13)
                .foregroundStyle(destructive ? DesignColor.red.color : DesignColor.mut.color)
                .frame(width: 26, height: 24)
                .background(hovering ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.control))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(help)
        .accessibilityLabel(help)
    }
}

struct SettingsChip: View {
    let text: String
    var accent = false

    var body: some View {
        Text(text)
            .font(.design(DesignTypography.captionSize, mono: true))
            .foregroundStyle(accent ? DesignColor.acc.color : DesignColor.mut.color)
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background(accent ? DesignColor.accbg.color : DesignColor.hov.color, in: RoundedRectangle(cornerRadius: DesignRadius.tag))
    }
}

struct SettingsCard<Content: View>: View {
    var tint: Color?
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap8) { content() }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(DesignSpacing.toastPadX)
            .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.panel))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.panel).strokeBorder(tint ?? DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }
}

struct SettingsSheet<Content: View>: View {
    let title: String
    var width: CGFloat = 440
    var error: String?
    var saveTitle: String?
    var canSave = true
    var busy = false
    var destructive = false
    var cancelTitle: String?
    let cancel: () -> Void
    var save: (() -> Void)?
    @ViewBuilder var content: () -> Content
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(title)
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            content()
            if let error {
                Text(error)
                    .foregroundStyle(DesignColor.red.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button(cancelTitle ?? l.string(.commonCancel), action: cancel)
                    .keyboardShortcut(save == nil ? .defaultAction : .cancelAction)
                if let save {
                    Button(saveTitle ?? l.string(.commonSave), role: destructive ? .destructive : nil, action: save)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!canSave || busy)
                }
            }
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: width)
        .font(.design(DesignTypography.baseSize))
        .background(DesignColor.panel.color)
    }
}

struct SettingsLabeled<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).foregroundStyle(DesignColor.mut.color)
            content()
        }
    }
}

struct SettingsRefusal: Identifiable {
    let id = UUID()
    var subject: String
    var uses: [SettingsUsage]
    var detail: String?
}

struct SettingsRefusalSheet: View {
    let refusal: SettingsRefusal
    let close: () -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        SettingsSheet(title: l.string(.settingsUsedTitle), width: 460, cancelTitle: l.string(.settingsUsedOk), cancel: close) {
            Text(l.string(.settingsUsedBody, refusal.subject))
                .fixedSize(horizontal: false, vertical: true)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(refusal.uses) { use in
                        HStack(alignment: .top, spacing: DesignSpacing.gap10) {
                            Text(label(use.kind))
                                .foregroundStyle(DesignColor.dim.color)
                                .frame(width: 130, alignment: .leading)
                            Text(use.name)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .padding(.vertical, 5)
                        .overlay(alignment: .bottom) { DesignDivider() }
                    }
                    if let detail = refusal.detail, refusal.uses.isEmpty {
                        Text(detail).foregroundStyle(DesignColor.mut.color)
                    }
                }
            }
            .frame(maxHeight: 220)
        }
    }

    private func label(_ kind: SettingsUsage.Kind) -> String {
        switch kind {
        case .type: l.string(.settingsUseType)
        case .tasks(let count): l.string(.settingsUseTasks, String(count))
        case .board(let board): l.string(.settingsUseBoard, board)
        case .field: l.string(.settingsUseField)
        }
    }
}
