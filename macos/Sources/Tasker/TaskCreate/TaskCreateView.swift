import AppKit
import SwiftUI
import TaskerAPI

struct TaskCreateSheet: View {
    let client: TaskerAPIClient
    let project: UUID
    let request: TaskCreationRequest
    let navigator: TaskNavigator
    @Environment(TaskCreationCoordinator.self) private var creation
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @State private var model: TaskCreateModel?

    var body: some View {
        Group {
            if let model {
                TaskCreateView(model: model) { creation.dismiss() }
            } else {
                Color.clear
            }
        }
        .frame(width: TaskCreateView.width, height: TaskCreateView.height)
        .background(DesignColor.panel.color)
        .interactiveDismissDisabled()
        .task {
            let value = TaskCreateModel(project: project, request: request, source: GraphQLTaskCreateSource(client: client))
            value.localizer = l
            value.onCreated = { [live, creation, navigator, l] created, reference in
                live.show(.notice(l.string(.taskCreateCreated, reference)))
                creation.dismiss()
                navigator.open(created.id)
            }
            model = value
            value.load()
        }
        .onChange(of: l.language) { model?.localizer = l }
        .onDisappear { model?.stop() }
    }
}

struct TaskCreateView: View {
    let model: TaskCreateModel
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var confirmDiscard = false

    static let width: CGFloat = 880
    static let height: CGFloat = 600

    var body: some View {
        VStack(spacing: 0) {
            header
            switch model.phase {
            case .loading:
                LiveMessageView(text: l.string(.taskListLoading))
            case .failed(let message):
                VStack(spacing: DesignSpacing.gap10) {
                    Text(l.string(.taskDetailLoadFailed, message))
                        .foregroundStyle(DesignColor.mut.color)
                        .multilineTextAlignment(.center)
                    Button(l.string(.taskListRetry)) { model.load() }
                }
                .padding(DesignSpacing.pagePadX)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .ready:
                HStack(spacing: 0) {
                    TaskCreateMain(model: model)
                    DesignDivider(vertical: true)
                    ScrollView {
                        TaskCreateProperties(model: model)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 20)
                    }
                    .frame(width: 290)
                }
                .disabled(model.submitting)
            }
            footer
        }
        .font(.design(DesignTypography.baseSize))
        .foregroundStyle(DesignColor.tx.color)
        .confirmationDialog(l.string(.taskCreateDiscardTitle), isPresented: $confirmDiscard, titleVisibility: .visible) {
            Button(l.string(.taskCreateDiscard), role: .destructive, action: close)
            Button(l.string(.taskCreateKeepEditing), role: .cancel) {}
        } message: {
            Text(l.string(.taskCreateDiscardMessage))
        }
    }

    private func requestClose() {
        if model.isDirty {
            confirmDiscard = true
        } else {
            close()
        }
    }

    private func save() {
        NSApp.keyWindow?.makeFirstResponder(nil)
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(60))
            await model.submit()
        }
    }

    private var header: some View {
        HStack(spacing: DesignSpacing.gap8) {
            Text(l.string(.taskCreateTitle)).foregroundStyle(DesignColor.mut.color)
            Spacer(minLength: 0)
            Button(action: requestClose) {
                MockupIconView(icon: .x, size: 13)
                    .foregroundStyle(DesignColor.mut.color)
                    .frame(width: 24, height: 24)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(l.string(.taskDetailClose))
        }
        .padding(.leading, 20)
        .padding(.trailing, 12)
        .frame(height: 40)
        .overlay(alignment: .bottom) { DesignDivider() }
    }

    private var footer: some View {
        HStack(spacing: DesignSpacing.gap8) {
            if let text = model.problem ?? model.serverError {
                Text(text)
                    .foregroundStyle(DesignColor.red.color)
                    .lineLimit(2)
                    .textSelection(.enabled)
            }
            Spacer(minLength: 0)
            Text(verbatim: "⌘↵").font(.design(DesignTypography.kbdSize)).foregroundStyle(DesignColor.dim.color)
            Button(l.string(.commonCancel), action: requestClose)
                .keyboardShortcut(.cancelAction)
            Button(action: save) {
                HStack(spacing: 6) {
                    if model.submitting { ProgressView().controlSize(.small) }
                    Text(l.string(.commonSave))
                }
            }
            .keyboardShortcut(.return, modifiers: .command)
            .buttonStyle(.borderedProminent)
            .disabled(model.phase != .ready || model.submitting)
        }
        .padding(.horizontal, 16)
        .frame(height: 48)
        .overlay(alignment: .top) { DesignDivider() }
    }
}

struct TaskCreateMain: View {
    let model: TaskCreateModel
    @Environment(\.localizer) private var l
    @FocusState private var focus: Field?
    @State private var dragStart: CGFloat?
    @State private var splitterActive = false
    @State private var linking = false

    private enum Field { case title, description }

    var body: some View {
        GeometryReader { proxy in
            let limit = max(TaskCreateModel.minLinksHeight, proxy.size.height - 120 - TaskCreateModel.minDescriptionHeight)
            let height = min(max(model.linksHeight, TaskCreateModel.minLinksHeight), limit)
            VStack(alignment: .leading, spacing: 0) {
                TextField(l.string(.taskDetailTitlePlaceholder), text: Bindable(model).title, axis: .vertical)
                    .textFieldStyle(.plain)
                    .font(.design(DesignTypography.pageTitleSize - 1, weight: DesignTypography.semiboldWeight))
                    .lineLimit(1...3)
                    .focused($focus, equals: .title)
                    .onSubmit { focus = .description }
                    .padding(.bottom, DesignSpacing.gap10)
                    .overlay(alignment: .bottomLeading) {
                        if model.attempted, model.titleMissing {
                            DesignColor.red.color.frame(height: DesignLayout.borderWidth)
                        }
                    }
                ZStack(alignment: .topLeading) {
                    TextEditor(text: Bindable(model).description)
                        .font(.design(DesignTypography.baseSize))
                        .scrollContentBackground(.hidden)
                        .focused($focus, equals: .description)
                        .padding(.horizontal, -5)
                    if model.description.isEmpty {
                        Text(l.string(.taskDetailDescriptionPlaceholder))
                            .foregroundStyle(DesignColor.dim.color)
                            .allowsHitTesting(false)
                    }
                }
                .frame(maxHeight: .infinity)
                splitter(limit: limit, height: height)
                links
                    .frame(height: height)
            }
            .padding(.horizontal, 28)
            .padding(.top, 22)
        }
        .frame(maxWidth: .infinity)
        .onAppear { focus = .title }
    }

    private func splitter(limit: CGFloat, height: CGFloat) -> some View {
        ZStack {
            DesignColor.line2.color.frame(height: DesignLayout.borderWidth)
            Capsule().fill(DesignColor.line2.color).frame(width: 36, height: 4)
        }
        .frame(height: 13)
        .padding(.horizontal, -28)
        .overlay {
            if splitterActive {
                ZStack {
                    DesignColor.acc.color.frame(height: DesignLayout.borderWidth)
                    Capsule().fill(DesignColor.acc.color).frame(width: 36, height: 4)
                }
            }
        }
        .contentShape(Rectangle())
        .onHover { inside in
            splitterActive = inside || dragStart != nil
            if inside { NSCursor.resizeUpDown.push() } else { NSCursor.pop() }
        }
        .gesture(
            DragGesture(minimumDistance: 1, coordinateSpace: .global)
                .onChanged { value in
                    let start = dragStart ?? height
                    dragStart = start
                    splitterActive = true
                    model.linksHeight = min(max(start - value.translation.height, TaskCreateModel.minLinksHeight), limit)
                }
                .onEnded { _ in
                    dragStart = nil
                    splitterActive = false
                }
        )
        .help(l.string(.taskCreateSplitter))
        .accessibilityLabel(l.string(.taskCreateSplitter))
    }

    private var links: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: DesignSpacing.gap8) {
                    Text(l.string(.taskDetailLinks).uppercased(with: l.locale))
                        .font(.design(DesignTypography.smallSize))
                        .tracking(0.6)
                        .foregroundStyle(DesignColor.mut.color)
                    Spacer(minLength: 0)
                    GhostButton(title: l.string(.taskDetailLink), icon: .plus) { linking = true }
                        .popover(isPresented: $linking, arrowEdge: .bottom) {
                            LinkPicker(
                                vocabulary: model.vocabulary, heading: l.string(.taskCreateTitle), preset: .any,
                                search: { [source = model.source, project = model.project] in (try? await source.search(project: project)) ?? [] },
                                excluded: { model.linkedIds(type: $0.typeId, outward: $0.outward) },
                                choose: { side, brief in model.addLink(type: side.typeId, outward: side.outward, task: brief) },
                                done: { linking = false })
                        }
                }
                .padding(.vertical, DesignSpacing.gap6)
                let groups = model.linkGroups
                if groups.isEmpty {
                    Text(l.string(.taskDetailNoLinks))
                        .foregroundStyle(DesignColor.dim.color)
                        .padding(.vertical, 4)
                }
                ForEach(groups) { group in
                    Text(verbatim: group.name)
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                        .padding(.top, DesignSpacing.gap6)
                        .padding(.bottom, 2)
                    ForEach(group.entries) { entry in
                        LinkedTaskRow(brief: entry.task, vocabulary: model.vocabulary, open: { _ in }) {
                            model.removeLink(DraftLink(typeId: entry.typeId, outward: entry.direction == .outward, task: entry.task).id)
                        }
                    }
                }
            }
            .padding(.bottom, 12)
        }
    }
}

struct TaskCreateProperties: View {
    let model: TaskCreateModel
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            statusRow
            typeRow
            if !model.typeFields.isEmpty { PropertyGap() }
            ForEach(model.typeFields) { field in
                FieldRowView(
                    field: field, vocabulary: model.vocabulary,
                    highlight: model.attempted && ((field.required && field.isEmpty) || model.invalidFields.contains { $0.fieldId == field.fieldId }),
                    popoverChoices: true,
                    set: { model.setValues(field.fieldId, $0) })
            }
            PropertyGap()
            seriesRow
        }
        .font(.design(DesignTypography.baseSize))
    }

    private var statusRow: some View {
        PropertyRow(label: Text(l.string(.taskDetailStatus))) {
            Menu {
                ForEach(model.statusOptions, id: \.id) { status in
                    Button {
                        model.setStatus(status.id)
                    } label: {
                        if status.id == model.statusId { Label(status.name, systemImage: "checkmark") } else { Text(status.name) }
                    }
                }
            } label: {
                let status = model.statusId.flatMap { model.vocabulary.status($0) }
                HStack(spacing: 6) {
                    if let statusId = model.statusId, let typeId = model.typeId {
                        GlyphView(
                            glyph: model.vocabulary.glyph(status: statusId, type: typeId),
                            color: status.map { Color(designHex: $0.color) } ?? DesignColor.dim.color)
                    }
                    Text(status?.name ?? "—")
                }
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
        }
    }

    private var typeRow: some View {
        PropertyRow(label: Text(l.string(.taskDetailType))) {
            Menu {
                ForEach(model.vocabulary.typeOrder, id: \.self) { id in
                    if let type = model.vocabulary.types[id] {
                        Button {
                            model.setType(id)
                        } label: {
                            if id == model.typeId { Label(type.name, systemImage: "checkmark") } else { Text(type.name) }
                        }
                    }
                }
            } label: {
                let index = model.typeId.flatMap { model.vocabulary.typeOrder.firstIndex(of: $0) } ?? 0
                TagLabel(
                    text: model.typeId.flatMap { model.vocabulary.types[$0]?.name } ?? "—",
                    hex: DesignTag.palette[(index + 1) % DesignTag.palette.count])
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
        }
    }

    private var seriesRow: some View {
        PropertyRow(label: Text(l.string(.taskDetailSeries)), tappable: false) {
            HStack(spacing: 4) {
                if model.seriesIds.isEmpty {
                    Text(l.string(.taskCreateNoSeries)).foregroundStyle(DesignColor.dim.color)
                }
                ForEach(model.seriesIds, id: \.self) { id in
                    Button {
                        model.removeSeries(id)
                    } label: {
                        HStack(spacing: 4) {
                            Text(model.vocabulary.prefixes[id] ?? model.vocabulary.seriesNames[id] ?? "?")
                                .font(.design(DesignTypography.monoSize, mono: true))
                            MockupIconView(icon: .x, size: 9)
                        }
                        .foregroundStyle(DesignColor.acc.color)
                        .padding(.horizontal, DesignSpacing.gap8)
                        .frame(height: 20)
                        .background(DesignColor.accbg.color, in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.taskDetailRemove))
                }
                Menu {
                    ForEach(model.freeSeries, id: \.self) { id in
                        Button(model.vocabulary.prefixes[id] ?? model.vocabulary.seriesNames[id] ?? id.uuidString) {
                            model.addSeries(id)
                        }
                    }
                } label: {
                    MockupIconView(icon: .plus, size: 10)
                        .foregroundStyle(DesignColor.mut.color)
                        .frame(width: 20, height: 20)
                        .overlay(Capsule().strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
                }
                .menuStyle(.button)
                .buttonStyle(.plain)
                .menuIndicator(.hidden)
                .disabled(model.freeSeries.isEmpty)
                .help(l.string(.taskDetailSeriesAdd))
            }
        }
    }
}
