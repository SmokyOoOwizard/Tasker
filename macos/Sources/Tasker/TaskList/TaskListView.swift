import SwiftUI
import TaskerAPI

struct TaskListSection: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(TaskCreationCoordinator.self) private var creation
    @Environment(WorkspaceTabSession.self) private var session

    private var navigator: TaskNavigator { session.navigator }

    private var pending: Binding<TaskCreationRequest?> {
        Binding(
            get: { live.client != nil && selection.current != nil ? creation.request : nil },
            set: { creation.request = $0 }
        )
    }

    var body: some View {
        ProjectScope {
            if let client = live.client, let project = selection.current {
                TaskWorkArea(client: client, project: project, navigator: navigator)
                    .id(project.id)
            }
        }
        .sheet(item: pending) { request in
            if let client = live.client, let project = selection.current {
                TaskCreateSheet(client: client, project: project.id, request: request, navigator: navigator)
            }
        }
        .onChange(of: selection.current?.id, initial: true) {
            session.noteProject(selection.current?.id)
        }
    }
}

struct TaskWorkArea: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    let navigator: TaskNavigator

    var body: some View {
        HStack(spacing: 0) {
            ZStack {
                TaskListScreen(client: client, project: project, navigator: navigator)
                    .opacity(navigator.card == nil ? 1 : 0)
                    .allowsHitTesting(navigator.card == nil)
                    .accessibilityHidden(navigator.card != nil)
                if let card = navigator.card {
                    TaskCardScreen(client: client, project: project.id, task: card, navigator: navigator)
                }
            }
            if navigator.card == nil, let peek = navigator.peek {
                TaskPeekPanel(client: client, project: project.id, task: peek, navigator: navigator)
                    .transition(.move(edge: .trailing))
            }
        }
        .animation(.easeInOut(duration: 0.18), value: navigator.peek)
    }
}

struct TaskListScreen: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    let navigator: TaskNavigator
    @Environment(LiveWorkspace.self) private var live
    @Environment(TaskListSettings.self) private var settings
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var model: TaskListModel?
    @State private var owner = UUID()

    var body: some View {
        VStack(spacing: 0) {
            if let model {
                if let series = session.seriesPage {
                    SeriesPageHeader(client: client, project: project.id, series: series) { model.scheduleRefresh() }
                }
                TaskListQueryBar(model: model) {
                    if let query = session.listQuery {
                        TaskQueryBarField(model: query)
                    }
                }
                .zIndex(1)
                ZStack {
                    TaskOutline(model: model, localizer: l) { session.notice($0) }
                    TaskListStateOverlay(model: model)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task {
            model = session.attachList(owner: owner, project: project.id, client: client, localizer: l)
        }
        .onChange(of: live.revision) {
            session.listQuery?.recheck()
            model?.scheduleRefresh()
        }
        .onChange(of: settings.revision) { model?.apply(settings.preferences(for: project.id)) }
        .onChange(of: l.language) {
            model?.localizer = l
            model?.apply(settings.preferences(for: project.id))
        }
        .onDisappear { session.detachList(owner: owner) }
    }
}

private struct TaskListStateOverlay: View {
    let model: TaskListModel
    @Environment(\.localizer) private var l

    var body: some View {
        switch model.phase {
        case .loading:
            LiveMessageView(text: l.string(.taskListLoading))
                .background(DesignColor.bg.color)
        case .failed(let message):
            VStack(spacing: DesignSpacing.gap10) {
                Text(l.string(.taskListLoadFailed, message))
                    .foregroundStyle(DesignColor.mut.color)
                    .multilineTextAlignment(.center)
                Button(l.string(.taskListRetry)) { model.reload() }
            }
            .padding(DesignSpacing.pagePadX)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(DesignColor.bg.color)
        case .ready:
            if model.roots.isEmpty {
                VStack(spacing: DesignSpacing.gap10) {
                    MockupIconView(icon: .inbox, size: 28)
                        .foregroundStyle(DesignColor.dim.color)
                    Text(l.string(model.filter == .none && model.query.isEmpty ? .taskListEmptyProject : .taskListEmpty))
                        .foregroundStyle(DesignColor.mut.color)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(DesignColor.bg.color)
            }
        }
    }
}

struct TaskListQueryBar<Query: View>: View {
    let model: TaskListModel
    @ViewBuilder var query: () -> Query
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap6) {
            HStack(alignment: .center, spacing: DesignSpacing.gap8) {
                query()
                if !model.queryHasSort {
                    TaskSortPills(model: model)
                }
                Spacer(minLength: 0)
                if let total = model.totalCount {
                    Text(l.string(.taskListFound, l.number(Double(total))))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.dim.color)
                        .monospacedDigit()
                        .lineLimit(1)
                        .fixedSize()
                }
            }
        }
        .padding(.horizontal, DesignSpacing.pagePadX)
        .padding(.top, DesignSpacing.gap10)
        .padding(.bottom, DesignSpacing.gap8)
        .frame(minHeight: DesignLayout.topBarHeight)
        .overlay(alignment: .bottom) { DesignDivider() }
    }
}

extension TaskListQueryBar where Query == EmptyView {
    init(model: TaskListModel) {
        self.init(model: model) { EmptyView() }
    }
}

private struct TaskSortPills: View {
    let model: TaskListModel
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: DesignSpacing.gap6) {
            ForEach(model.preferences.sort, id: \.key) { key in
                HStack(spacing: 4) {
                    Button {
                        model.update { $0.sort = TaskSort.toggled($0.sort, key.key) }
                    } label: {
                        HStack(spacing: 4) {
                            MockupIconView(icon: key.descending ? .down : .up, size: 11)
                            Text(TaskSortField(key: key.key).title(l))
                        }
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.taskListSortToggle))
                    Button {
                        model.update { $0.sort = TaskSort.removed($0.sort, key.key) }
                    } label: {
                        MockupIconView(icon: .x, size: 10)
                            .foregroundStyle(DesignColor.dim.color)
                            .frame(width: 16, height: 16)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.taskListSortRemove))
                }
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.tx.color)
                .padding(.horizontal, DesignSpacing.gap8)
                .frame(height: DesignLayout.tagHeight + 2)
                .overlay(RoundedRectangle(cornerRadius: DesignRadius.pill).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
            }
        }
    }
}

struct TaskListTopActions: View {
    @Environment(ProjectSelection.self) private var selection
    @Environment(TaskListSettings.self) private var settings
    @Environment(TaskCreationCoordinator.self) private var creation
    @Environment(\.localizer) private var l
    @State private var sorting = false
    @Environment(WorkspaceTabSession.self) private var session

    var body: some View {
        if let project = selection.current?.id {
            let preferences = settings.preferences(for: project)
            HStack(spacing: DesignSpacing.gap8) {
                GhostButton(title: l.string(.taskCreateAction), icon: .plus) { creation.present() }
                    .help(l.string(.taskCreateAction) + " ⌘N")
                Button {
                    sorting.toggle()
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .up, size: 12)
                        Text(l.string(.taskListSort))
                    }
                    .foregroundStyle(preferences.sort.isEmpty ? DesignColor.mut.color : DesignColor.tx.color)
                    .padding(.horizontal, DesignSpacing.gap8)
                    .frame(height: 24)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(session.listQuery?.hasSort ?? false)
                .opacity(session.listQuery?.hasSort == true ? 0.4 : 1)
                .popover(isPresented: $sorting, arrowEdge: .bottom) {
                    TaskSortPicker(project: project)
                }
                TaskModePicker(selection: preferences.mode) { mode in
                    settings.update(project) { $0.mode = mode }
                }
            }
            .font(.design(DesignTypography.smallSize))
        }
    }
}

private struct TaskModePicker: View {
    let selection: TaskListMode
    let choose: (TaskListMode) -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: 0) {
            ForEach(TaskListMode.allCases) { mode in
                Button {
                    choose(mode)
                } label: {
                    Text(l.string(mode.titleKey))
                        .foregroundStyle(mode == selection ? DesignColor.tx.color : DesignColor.mut.color)
                        .padding(.horizontal, DesignSpacing.gap8)
                        .frame(height: 22)
                        .background(RoundedRectangle(cornerRadius: DesignRadius.chip).fill(mode == selection ? DesignColor.sel.color : .clear))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(mode == selection ? .isSelected : [])
            }
        }
        .padding(2)
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
        .help(l.string(.taskListView))
    }
}

private struct TaskSortPicker: View {
    let project: UUID
    @Environment(TaskListSettings.self) private var settings
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @State private var search = ""
    @State private var fields: [TaskSortField] = TaskSortField.builtIn
    @FocusState private var focused: Bool

    var body: some View {
        let sort = settings.preferences(for: project).sort
        VStack(alignment: .leading, spacing: 0) {
            Text(l.string(.taskListSortHint).uppercased(with: l.locale))
                .font(.design(DesignTypography.captionSize, weight: DesignTypography.semiboldWeight))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.top, DesignSpacing.gap10)
            TextField(l.string(.taskListSortSearch), text: $search)
                .textFieldStyle(.plain)
                .focused($focused)
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.vertical, DesignSpacing.gap10)
            DesignDivider()
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(visible, id: \.key) { field in
                        let index = sort.firstIndex { $0.key == field.key }
                        Button {
                            settings.update(project) { $0.sort = TaskSort.cycled($0.sort, field.key) }
                        } label: {
                            HStack(spacing: DesignSpacing.gap8) {
                                Group {
                                    if let index {
                                        MockupIconView(icon: sort[index].descending ? .down : .up, size: 12)
                                            .foregroundStyle(DesignColor.acc.color)
                                    } else {
                                        Color.clear
                                    }
                                }
                                .frame(width: 14)
                                Text(field.title(l))
                                    .foregroundStyle(DesignColor.tx.color)
                                Spacer(minLength: 0)
                                if let index, sort.count > 1 {
                                    Text(verbatim: String(index + 1))
                                        .foregroundStyle(DesignColor.dim.color)
                                        .monospacedDigit()
                                }
                            }
                            .padding(.horizontal, DesignSpacing.gap10)
                            .frame(height: 28)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(DesignSpacing.gap6)
            }
            .frame(maxHeight: 320)
            if !sort.isEmpty {
                DesignDivider()
                Button(l.string(.taskListSortReset)) {
                    settings.update(project) { $0.sort = [] }
                }
                .buttonStyle(.plain)
                .foregroundStyle(DesignColor.mut.color)
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.vertical, DesignSpacing.gap8)
            }
        }
        .font(.design(DesignTypography.baseSize))
        .frame(width: 340)
        .onAppear { focused = true }
        .task {
            guard let client = live.client, let vocabulary = try? await GraphQLTaskListSource(client: client).vocabulary(project: project) else { return }
            fields = vocabulary.sortFields()
        }
    }

    private var visible: [TaskSortField] {
        let text = search.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return fields }
        return fields.filter { $0.title(l).localizedCaseInsensitiveContains(text) || $0.key.localizedCaseInsensitiveContains(text) }
    }
}
