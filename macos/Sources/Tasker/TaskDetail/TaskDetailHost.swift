import SwiftUI
import TaskerAPI

struct TaskDetailHost<Chrome: View>: View {
    let client: TaskerAPIClient
    let project: UUID
    let task: UUID
    let compact: Bool
    let open: (UUID) -> Void
    let deleted: () -> Void
    @ViewBuilder var chrome: (TaskDetailModel) -> Chrome
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var model: TaskDetailModel?

    var body: some View {
        VStack(spacing: 0) {
            if let model {
                chrome(model)
                TaskPageView(model: model, compact: compact, open: open)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(compact ? DesignColor.panel.color : DesignColor.bg.color)
        .task(id: task) {
            model?.stop()
            let value = TaskDetailModel(project: project, task: task, source: GraphQLTaskDetailSource(client: client))
            value.localizer = l
            value.onNotice = { [session] text in session.notice(text) }
            value.onFailure = { [session] text in session.failure(text) }
            value.onDeleted = deleted
            value.drafts = session.drafts
            model = value
            value.load()
        }
        .onChange(of: live.revision) { model?.refresh() }
        .onChange(of: l.language) { model?.localizer = l }
        .onDisappear { model?.stop() }
    }
}

struct TaskCardScreen: View {
    let client: TaskerAPIClient
    let project: UUID
    let task: UUID
    let navigator: TaskNavigator
    @Environment(\.localizer) private var l
    @FocusState private var focused: Bool

    var body: some View {
        TaskDetailHost(
            client: client, project: project, task: task, compact: false,
            open: { navigator.open($0) },
            deleted: { navigator.closeCard() }
        ) { model in
            HStack(spacing: DesignSpacing.gap8) {
                Button {
                    navigator.back()
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .back, size: 12)
                        Text(l.string(.taskDetailBack))
                        Text(verbatim: "⎋").font(.design(DesignTypography.kbdSize)).foregroundStyle(DesignColor.dim.color)
                    }
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
                    .padding(.horizontal, DesignSpacing.gap8)
                    .frame(height: 24)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                ScrollViewReader { proxy in
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: DesignSpacing.gap8) {
                            Button {
                                navigator.closeCard()
                            } label: {
                                Text(l.string(.sectionTasks))
                                    .foregroundStyle(DesignColor.mut.color)
                                    .padding(.horizontal, 4)
                                    .frame(height: 24)
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            ForEach(Array(navigator.history.enumerated()), id: \.offset) { index, id in
                                Text(l.string(.breadcrumbSeparator)).foregroundStyle(DesignColor.dim.color)
                                Button {
                                    navigator.jump(toHistory: index)
                                } label: {
                                    Text(verbatim: navigator.labels[id] ?? "…")
                                        .font(.design(DesignTypography.monoSize, mono: true))
                                        .foregroundStyle(DesignColor.mut.color)
                                        .padding(.horizontal, 4)
                                        .frame(height: 24)
                                        .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
                            }
                            Text(l.string(.breadcrumbSeparator)).foregroundStyle(DesignColor.dim.color)
                            Text(verbatim: model.readyReference ?? navigator.labels[task] ?? "…")
                                .font(.design(DesignTypography.monoSize, mono: true))
                                .onAppear { register(model) }
                                .onChange(of: model.revision) { register(model) }
                            Color.clear.frame(width: 1, height: 1).id("crumbs-end")
                        }
                        .fixedSize()
                    }
                    .onAppear { proxy.scrollTo("crumbs-end", anchor: .trailing) }
                    .onChange(of: navigator.history) { proxy.scrollTo("crumbs-end", anchor: .trailing) }
                    .onChange(of: navigator.card) { proxy.scrollTo("crumbs-end", anchor: .trailing) }
                }
                Spacer(minLength: 0)
            }
            .font(.design(DesignTypography.baseSize))
            .padding(.horizontal, DesignSpacing.pagePadX)
            .frame(height: DesignLayout.topBarHeight)
            .overlay(alignment: .bottom) { DesignDivider() }
        }
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.escape) {
            navigator.back()
            return .handled
        }
        .onAppear { focused = true }
        .onChange(of: task) { focused = true }
    }
}

extension TaskCardScreen {
    fileprivate func register(_ model: TaskDetailModel) {
        guard let detail = model.detail else { return }
        navigator.label(detail.id, model.reference)
        for link in detail.links {
            navigator.label(link.task.id, model.vocabulary.reference(link.task.seriesNumbers, id: link.task.id))
        }
    }
}

struct TaskPeekPanel: View {
    let client: TaskerAPIClient
    let project: UUID
    let task: UUID
    let navigator: TaskNavigator
    @Environment(\.localizer) private var l

    static let width: CGFloat = 520

    var body: some View {
        TaskDetailHost(
            client: client, project: project, task: task, compact: true,
            open: { navigator.showPeek($0) },
            deleted: { navigator.closePeek() }
        ) { model in
            HStack(spacing: DesignSpacing.gap8) {
                Text(verbatim: model.reference)
                    .font(.design(DesignTypography.monoSize, mono: true))
                    .foregroundStyle(DesignColor.mut.color)
                Spacer(minLength: 0)
                GhostButton(title: l.string(.taskDetailOpen) + " ↵") { navigator.open(task) }
                Button {
                    navigator.closePeek()
                } label: {
                    MockupIconView(icon: .x, size: 13)
                        .foregroundStyle(DesignColor.mut.color)
                        .frame(width: 24, height: 24)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(l.string(.taskDetailClose))
            }
            .padding(.horizontal, 14)
            .frame(height: 44)
            .overlay(alignment: .bottom) { DesignDivider() }
        }
        .frame(width: Self.width)
        .overlay(alignment: .leading) { DesignDivider(vertical: true) }
    }
}
