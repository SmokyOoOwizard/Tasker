import SwiftUI
import TaskerAPI

struct TaskPageView: View {
    let model: TaskDetailModel
    let compact: Bool
    let open: (UUID) -> Void
    @Environment(\.localizer) private var l

    var body: some View {
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
        case .missing:
            VStack(spacing: DesignSpacing.gap10) {
                MockupIconView(icon: .inbox, size: 28).foregroundStyle(DesignColor.dim.color)
                Text(l.string(.taskDetailMissing)).foregroundStyle(DesignColor.mut.color)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .ready:
            if let detail = model.detail {
                ready(detail)
            }
        }
    }

    @ViewBuilder
    private func ready(_ detail: TaskDetailData) -> some View {
        Group {
            if compact {
                ScrollView {
                    VStack(spacing: 0) {
                        PropertiesColumn(model: model, detail: detail, compact: true)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 10)
                        DesignDivider()
                        MainColumn(model: model, detail: detail, compact: true, open: open)
                            .padding(.horizontal, 22)
                            .padding(.top, 18)
                            .padding(.bottom, 40)
                    }
                }
            } else {
                HStack(spacing: 0) {
                    ScrollView {
                        MainColumn(model: model, detail: detail, compact: false, open: open)
                            .padding(.horizontal, 36)
                            .padding(.top, 26)
                            .padding(.bottom, 60)
                            .frame(maxWidth: 820, alignment: .leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    DesignDivider(vertical: true)
                    ScrollView {
                        PropertiesColumn(model: model, detail: detail, compact: false)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 20)
                    }
                    .frame(width: 290)
                }
            }
        }
        .alert(l.string(.taskDetailConflictTitle), isPresented: Binding(
            get: { model.conflict != nil },
            set: { if !$0 { model.conflict = nil } }
        )) {
            Button(l.string(.taskDetailConflictApply)) { model.resolveConflict(keepMine: true) }
            Button(l.string(.taskDetailConflictKeep), role: .cancel) { model.resolveConflict(keepMine: false) }
        } message: {
            Text(l.string(.taskDetailConflictMessage))
        }
    }
}

struct MainColumn: View {
    let model: TaskDetailModel
    let detail: TaskDetailData
    let compact: Bool
    let open: (UUID) -> Void
    @Environment(\.localizer) private var l
    @Environment(TaskCreationCoordinator.self) private var creation
    @State private var linkingAny = false
    @State private var linkingSubtask = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            TitleEditor(model: model, detail: detail, compact: compact)
            DescriptionEditor(model: model, detail: detail, open: openRef)
                .padding(.top, DesignSpacing.gap10)
            hierarchy
            links
        }
    }

    private func openRef(_ ref: String) {
        Task {
            let found = (try? await model.source.resolve(project: model.project, ref: ref)) ?? []
            if let first = found.first { open(first.id) }
        }
    }

    @ViewBuilder
    private var hierarchy: some View {
        let parents = model.parents
        let children = model.children
        let progress = model.childProgress
        SectionHeader(title: l.string(.taskDetailHierarchy)) {
            GhostButton(title: l.string(.taskDetailSubtask), icon: .plus) { linkingSubtask = true }
                .popover(isPresented: $linkingSubtask, arrowEdge: .bottom) {
                    LinkPicker(
                        model: model, preset: .subtask,
                        createNew: {
                            creation.present(parent: TaskLinkedBrief(
                                id: detail.id, title: detail.title, statusId: detail.statusId, typeId: detail.typeId, seriesNumbers: detail.seriesNumbers))
                        }
                    ) { linkingSubtask = false }
                }
        }
        if !parents.isEmpty {
            subtitle(Text(l.string(.taskDetailPartOf)))
            ForEach(parents) { entry in
                LinkedTaskRow(brief: entry.task, vocabulary: model.vocabulary, open: open) {
                    model.perform(.removeLink(type: entry.typeId, target: entry.task.id, outward: false))
                }
            }
        }
        if !children.isEmpty {
            HStack(spacing: DesignSpacing.gap10) {
                Text(l.string(.taskDetailIncludes))
                ProgressBar(fraction: progress.total == 0 ? 0 : Double(progress.done) / Double(progress.total))
                    .frame(maxWidth: 200)
                Text(l.string(.taskDetailProgress, String(progress.done), String(progress.total))).monospacedDigit()
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.mut.color)
            .padding(.top, DesignSpacing.gap6)
            .padding(.bottom, 2)
            ForEach(children) { entry in
                LinkedTaskRow(brief: entry.task, vocabulary: model.vocabulary, open: open) {
                    model.perform(.removeLink(type: entry.typeId, target: entry.task.id, outward: true))
                }
            }
        }
        if parents.isEmpty, children.isEmpty {
            Text(l.string(.taskDetailNoHierarchy))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.vertical, 4)
        }
    }

    @ViewBuilder
    private var links: some View {
        SectionHeader(title: l.string(.taskDetailLinks)) {
            GhostButton(title: l.string(.taskDetailLink), icon: .plus) { linkingAny = true }
                .popover(isPresented: $linkingAny, arrowEdge: .bottom) {
                    LinkPicker(model: model, preset: .any) { linkingAny = false }
                }
        }
        let groups = model.linkGroups
        if groups.isEmpty {
            Text(l.string(.taskDetailNoLinks))
                .foregroundStyle(DesignColor.dim.color)
                .padding(.vertical, 4)
        }
        ForEach(groups) { group in
            subtitle(Text(verbatim: group.name))
            ForEach(group.entries) { entry in
                LinkedTaskRow(brief: entry.task, vocabulary: model.vocabulary, open: open) {
                    model.perform(.removeLink(type: entry.typeId, target: entry.task.id, outward: entry.direction == .outward))
                }
            }
        }
    }

    private func subtitle(_ text: Text) -> some View {
        text
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.mut.color)
            .padding(.top, DesignSpacing.gap6)
            .padding(.bottom, 2)
    }
}

struct TitleEditor: View {
    let model: TaskDetailModel
    let detail: TaskDetailData
    let compact: Bool
    @Environment(\.localizer) private var l
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField(l.string(.taskDetailTitlePlaceholder), text: $draft, axis: .vertical)
            .textFieldStyle(.plain)
            .font(.design(compact ? DesignTypography.titleSize : DesignTypography.pageTitleSize - 1, weight: DesignTypography.semiboldWeight))
            .focused($focused)
            .lineLimit(1...6)
            .onSubmit(commit)
            .onExitCommand {
                draft = detail.title
                focused = false
            }
            .onChange(of: focused) { if !focused { commit() } }
            .onAppear {
                if let stored = slot?.text {
                    draft = stored
                    focused = true
                    TaskDraftCaret.moveToEnd()
                } else {
                    draft = detail.title
                }
            }
            .onChange(of: detail.title) { if !focused, slot?.text == nil { draft = detail.title } }
            .onChange(of: draft) { slot?.set(draft == detail.title ? nil : draft) }
    }

    private var slot: TaskDraftSlot? { model.draftSlot("title") }

    private func commit() {
        let value = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        slot?.set(nil)
        if value.isEmpty {
            draft = detail.title
            return
        }
        guard value != detail.title else { return }
        model.perform(.title(value))
    }
}

struct DescriptionEditor: View {
    let model: TaskDetailModel
    let detail: TaskDetailData
    let open: (String) -> Void
    @Environment(\.localizer) private var l
    @State private var editing = false
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        Group {
            if editing {
                TextEditor(text: $draft)
                    .font(.design(DesignTypography.baseSize))
                    .scrollContentBackground(.hidden)
                    .focused($focused)
                    .frame(minHeight: 140)
                    .padding(6)
                    .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
                    .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(DesignColor.acc.color, lineWidth: DesignLayout.borderWidth))
                    .onExitCommand {
                        editing = false
                    }
                    .onChange(of: focused) { if !focused { commit() } }
                    .onAppear { focused = true }
            } else {
                Group {
                    if detail.description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text(l.string(.taskDetailDescriptionPlaceholder))
                            .foregroundStyle(DesignColor.dim.color)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        MarkdownView(text: detail.description, prefixes: Set(model.vocabulary.prefixes.values), open: open)
                    }
                }
                .frame(minHeight: 110, alignment: .topLeading)
                .contentShape(Rectangle())
                .onTapGesture {
                    draft = detail.description
                    editing = true
                }
            }
        }
        .onAppear {
            if let stored = slot?.text {
                draft = stored
                editing = true
            }
        }
        .onChange(of: draft) { if editing { slot?.set(draft == detail.description ? nil : draft) } }
        .onChange(of: editing) { if !editing { slot?.set(nil) } }
    }

    private var slot: TaskDraftSlot? { model.draftSlot("description") }

    private func commit() {
        guard editing else { return }
        editing = false
        guard draft != detail.description else { return }
        model.perform(.description(draft))
    }
}

struct PropertiesColumn: View {
    let model: TaskDetailModel
    let detail: TaskDetailData
    let compact: Bool
    @Environment(\.localizer) private var l
    @State private var ownField = false
    @State private var typeChoice: UUID?
    @State private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            statusRow
            typeRow
            PropertyGap()
            ForEach(model.typeFields) { FieldRowView(model: model, field: $0) }
            if !model.extraFields.isEmpty { PropertyGap() }
            ForEach(model.extraFields) { FieldRowView(model: model, field: $0) }
            addFieldRow
            PropertyGap()
            SeriesRow(model: model, detail: detail)
            PropertyGap()
            PropertyRow(label: Text(l.string(.taskDetailCreated)), tappable: false) {
                Text(l.dateTime(detail.createdAt)).foregroundStyle(DesignColor.mut.color)
            }
            PropertyRow(label: Text(l.string(.taskDetailUpdated)), tappable: false) {
                Text(l.dateTime(detail.updatedAt)).foregroundStyle(DesignColor.mut.color)
            }
            PropertyGap()
            actions
        }
        .font(.design(DesignTypography.baseSize))
        .sheet(isPresented: $ownField) { OwnFieldSheet(model: model, presented: $ownField) }
        .sheet(item: $typeChoice) { type in
            StatusChoiceSheet(model: model, type: type) { status in
                typeChoice = nil
                if let status { model.perform(.type(type, status: status)) }
            }
        }
        .confirmationDialog(l.string(.taskDetailDeleteTitle, model.reference), isPresented: $confirmDelete, titleVisibility: .visible) {
            Button(l.string(.taskDetailDelete), role: .destructive) { model.perform(.delete) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: {
            Text(l.string(.taskDetailDeleteMessage, detail.title))
        }
    }

    private var statusRow: some View {
        PropertyRow(label: Text(l.string(.taskDetailStatus))) {
            Menu {
                ForEach(model.statusOptions, id: \.id) { status in
                    Button {
                        if status.id != detail.statusId { model.perform(.status(status.id)) }
                    } label: {
                        if status.id == detail.statusId { Label(status.name, systemImage: "checkmark") } else { Text(status.name) }
                    }
                }
            } label: {
                let status = model.vocabulary.status(detail.statusId)
                HStack(spacing: 6) {
                    GlyphView(
                        glyph: model.vocabulary.glyph(status: detail.statusId, type: detail.typeId),
                        color: status.map { Color(designHex: $0.color) } ?? DesignColor.dim.color)
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
                            guard id != detail.typeId else { return }
                            if model.vocabulary.statusNeedsChoice(current: detail.statusId, newType: id) {
                                typeChoice = id
                            } else {
                                model.perform(.type(id, status: nil))
                            }
                        } label: {
                            if id == detail.typeId { Label(type.name, systemImage: "checkmark") } else { Text(type.name) }
                        }
                    }
                }
            } label: {
                let index = model.vocabulary.typeOrder.firstIndex(of: detail.typeId) ?? 0
                TagLabel(text: model.vocabulary.types[detail.typeId]?.name ?? "—", hex: DesignTag.palette[(index + 1) % DesignTag.palette.count])
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
        }
    }

    private var addFieldRow: some View {
        Menu {
            ForEach(model.catalogFields, id: \.id) { field in
                Button(field.name) { model.perform(.addFields([field.id])) }
            }
            if !model.catalogFields.isEmpty { Divider() }
            Button(l.string(.taskDetailOwnField)) { ownField = true }
        } label: {
            PropertyRow(label: Text(Image(systemName: "plus")).foregroundStyle(DesignColor.dim.color)) {
                Text(l.string(.taskDetailAddField)).foregroundStyle(DesignColor.dim.color)
            }
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
    }

    private var actions: some View {
        Menu {
            Button(l.string(.taskDetailDeleteAction), role: .destructive) { confirmDelete = true }
        } label: {
            HStack(spacing: 4) {
                MockupIconView(icon: .more, size: 13)
                Text(l.string(.taskDetailActions))
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.gap8)
            .frame(height: 24)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
    }
}

extension UUID: @retroactive Identifiable {
    public var id: UUID { self }
}

struct StatusChoiceSheet: View {
    let model: TaskDetailModel
    let type: UUID
    let done: (UUID?) -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.taskDetailTypeChange, model.vocabulary.types[type]?.name ?? ""))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            Text(l.string(.taskDetailTypeChangeHint))
                .foregroundStyle(DesignColor.mut.color)
            VStack(spacing: 0) {
                ForEach(model.vocabulary.statusOptions(forType: type), id: \.id) { status in
                    Button {
                        done(status.id)
                    } label: {
                        HStack(spacing: DesignSpacing.gap8) {
                            GlyphView(glyph: model.vocabulary.glyph(status: status.id, type: type), color: Color(designHex: status.color))
                            Text(status.name)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, DesignSpacing.gap10)
                        .frame(height: 30)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
            HStack {
                Spacer()
                Button(l.string(.commonCancel)) { done(nil) }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(DesignSpacing.toastPadX)
        .frame(width: 320)
        .font(.design(DesignTypography.baseSize))
    }
}

struct SeriesRow: View {
    let model: TaskDetailModel
    let detail: TaskDetailData
    @Environment(\.localizer) private var l
    @State private var editing: UUID?
    @State private var number = ""

    var body: some View {
        PropertyRow(label: Text(l.string(.taskDetailSeries)), tappable: false) {
            HStack(spacing: 4) {
                ForEach(detail.seriesNumbers, id: \.seriesId) { entry in
                    Button {
                        number = String(entry.number)
                        editing = entry.seriesId
                    } label: {
                        Text(model.vocabulary.series(entry))
                            .font(.design(DesignTypography.monoSize, mono: true))
                            .foregroundStyle(DesignColor.acc.color)
                            .padding(.horizontal, DesignSpacing.gap8)
                            .frame(height: 20)
                            .background(DesignColor.accbg.color, in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .popover(isPresented: Binding(get: { editing == entry.seriesId }, set: { if !$0 { editing = nil } }), arrowEdge: .bottom) {
                        renumber(entry)
                    }
                }
                Menu {
                    ForEach(model.freeSeries, id: \.self) { id in
                        Button(model.vocabulary.prefixes[id] ?? model.vocabulary.seriesNames[id] ?? id.uuidString) {
                            model.perform(.addSeries(id))
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

    private func renumber(_ entry: TaskSeriesRef) -> some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap8) {
            Text(l.string(.taskDetailSeriesNumber, model.vocabulary.seriesNames[entry.seriesId] ?? model.vocabulary.prefixes[entry.seriesId] ?? ""))
                .foregroundStyle(DesignColor.mut.color)
            TextField(text: $number) { EmptyView() }
                .textFieldStyle(.roundedBorder)
                .frame(width: 140)
                .onSubmit { apply(entry) }
            HStack {
                Button(l.string(.taskDetailSeriesNext)) {
                    editing = nil
                    model.perform(.renumber(entry.seriesId, nil))
                }
                Button(l.string(.taskDetailSeriesApply)) { apply(entry) }
                    .disabled(Int(number.trimmingCharacters(in: .whitespaces)).map { $0 < 1 || $0 == entry.number } ?? true)
            }
            Divider()
            Button(l.string(.taskDetailSeriesRemove), role: .destructive) {
                editing = nil
                model.perform(.removeSeries(entry.seriesId))
            }
        }
        .padding(DesignSpacing.toastPadX)
        .font(.design(DesignTypography.baseSize))
    }

    private func apply(_ entry: TaskSeriesRef) {
        guard let value = Int(number.trimmingCharacters(in: .whitespaces)), value >= 1, value != entry.number else { return }
        editing = nil
        model.perform(.renumber(entry.seriesId, value))
    }
}

enum LinkPickerPreset: Identifiable {
    case any
    case subtask

    var id: Int { self == .any ? 0 : 1 }
}

struct LinkPicker: View {
    let vocabulary: TaskListVocabulary
    let heading: String
    let preset: LinkPickerPreset
    let search: () async -> [TaskLinkedBrief]
    let excluded: (Side) -> Set<UUID>
    let choose: (Side, TaskLinkedBrief) -> Void
    var createNew: (() -> Void)?
    let done: () -> Void
    @Environment(\.localizer) private var l
    @State private var query = ""
    @State private var all: [TaskLinkedBrief] = []
    @State private var loaded = false
    @State private var side: Side?
    @FocusState private var focused: Bool

    struct Side: Hashable, Identifiable {
        var typeId: UUID
        var outward: Bool
        var name: String
        var id: String { "\(typeId)\(outward)" }
    }

    private var sides: [Side] {
        var result: [Side] = []
        for type in vocabulary.linkTypes {
            if preset == .subtask, !type.hierarchical { continue }
            result.append(Side(typeId: type.id, outward: true, name: type.outwardName))
            if preset == .subtask { continue }
            if !type.isSymmetric, !type.inwardName.isEmpty, type.inwardName != type.outwardName {
                result.append(Side(typeId: type.id, outward: false, name: type.inwardName))
            }
        }
        return result
    }

    private var results: [TaskLinkedBrief] {
        let text = query.trimmingCharacters(in: .whitespaces)
        let chosen = side ?? sides.first
        let skipped = chosen.map(excluded) ?? []
        let base = all.filter { !skipped.contains($0.id) }
        guard !text.isEmpty else { return Array(base.prefix(50)) }
        return Array(base.filter {
            vocabulary.reference($0.seriesNumbers, id: $0.id).localizedCaseInsensitiveContains(text) || $0.title.localizedCaseInsensitiveContains(text)
        }.prefix(50))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(verbatim: heading)
                .font(.design(DesignTypography.baseSize, weight: DesignTypography.semiboldWeight))
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.top, DesignSpacing.gap10)
            Picker(l.string(.taskDetailLinkType), selection: Binding(get: { side ?? sides.first }, set: { side = $0 })) {
                ForEach(sides) { Text($0.name).tag(Optional($0)) }
            }
            .padding(.horizontal, DesignSpacing.toastPadX)
            .padding(.top, DesignSpacing.gap6)
            TextField(l.string(.taskDetailLinkSearch), text: $query)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.vertical, DesignSpacing.gap10)
            DesignDivider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    if let createNew {
                        Button {
                            done()
                            createNew()
                        } label: {
                            HStack(spacing: DesignSpacing.gap8) {
                                MockupIconView(icon: .plus, size: 12).foregroundStyle(DesignColor.acc.color)
                                Text(l.string(.taskCreateNewSubtask))
                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, DesignSpacing.gap10)
                            .frame(height: 28)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                    if !loaded {
                        Text(l.string(.taskListLoading)).foregroundStyle(DesignColor.dim.color).padding(DesignSpacing.gap10)
                    } else if results.isEmpty {
                        Text(l.string(.taskDetailLinkNothing)).foregroundStyle(DesignColor.dim.color).padding(DesignSpacing.gap10)
                    }
                    ForEach(results) { brief in
                        Button {
                            guard let chosen = side ?? sides.first else { return }
                            choose(chosen, brief)
                            done()
                        } label: {
                            HStack(spacing: DesignSpacing.gap8) {
                                GlyphView(
                                    glyph: brief.typeId.map { vocabulary.glyph(status: brief.statusId, type: $0) } ?? vocabulary.glyph(status: brief.statusId),
                                    color: vocabulary.status(brief.statusId).map { Color(designHex: $0.color) } ?? DesignColor.dim.color)
                                Text(vocabulary.reference(brief.seriesNumbers, id: brief.id))
                                    .font(.design(DesignTypography.monoSize, mono: true))
                                    .foregroundStyle(DesignColor.dim.color)
                                Text(brief.title).lineLimit(1)
                                Spacer(minLength: 0)
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
            .frame(height: 260)
        }
        .font(.design(DesignTypography.baseSize))
        .frame(width: 400)
        .onAppear { side = sides.first }
        .task {
            try? await Task.sleep(for: .milliseconds(150))
            focused = true
        }
        .task {
            all = await search()
            loaded = true
        }
    }
}

extension LinkPicker {
    init(model: TaskDetailModel, preset: LinkPickerPreset, createNew: (() -> Void)? = nil, done: @escaping () -> Void) {
        self.init(
            vocabulary: model.vocabulary,
            heading: model.localizer.string(.taskDetailLinkTitle, model.reference),
            preset: preset,
            search: { [source = model.source, project = model.project] in (try? await source.search(project: project)) ?? [] },
            excluded: { [weak model] side in
                var skipped = Set((model?.detail?.links ?? []).filter { $0.typeId == side.typeId }.map(\.task.id))
                if let id = model?.taskId { skipped.insert(id) }
                return skipped
            },
            choose: { side, brief in model.perform(.addLink(type: side.typeId, target: brief.id, outward: side.outward)) },
            createNew: createNew, done: done)
    }
}
