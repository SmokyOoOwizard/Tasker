import SwiftUI
import TaskerAPI

struct FieldRowView: View {
    let field: TaskFieldValue
    let vocabulary: TaskListVocabulary
    var highlight = false
    var popoverChoices = false
    var draftSlot: TaskDraftSlot?
    let set: ([String]) -> Void
    var remove: (() -> Void)?
    @Environment(\.localizer) private var l
    @State private var picking = false
    @State private var editing = false
    @State private var draft = ""
    @State private var drafts: [String] = []
    @State private var invalid = false
    @FocusState private var focused: Bool
    @State private var hover = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PropertyRow(label: label, tappable: !field.multiple || !editing) {
                HStack(spacing: DesignSpacing.gap6) {
                    content
                    Spacer(minLength: 0)
                    if field.source != .type, let remove, hover || editing {
                        Button {
                            remove()
                        } label: {
                            MockupIconView(icon: .x, size: 11)
                                .foregroundStyle(DesignColor.dim.color)
                                .frame(width: 18, height: 18)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help(l.string(.taskDetailFieldRemove))
                    }
                }
            }
            if editing, field.multiple {
                multipleEditor
                    .padding(.leading, 84 + DesignSpacing.gap8 + DesignSpacing.rowPadX)
                    .padding(.trailing, DesignSpacing.rowPadX)
                    .padding(.bottom, DesignSpacing.gap6)
            }
        }
        .onHover { hover = $0 }
        .onChange(of: field.values) { if !editing { draft = singleText } }
        .onAppear(perform: restoreDraft)
        .onChange(of: draft) { if editing, !field.multiple { draftSlot?.set(draft == singleText ? nil : draft) } }
        .onChange(of: drafts) { if editing, field.multiple { draftSlot?.set(list: drafts == field.values ? nil : drafts) } }
        .onChange(of: editing) { if !editing { draftSlot?.set(nil) } }
    }

    private func restoreDraft() {
        guard let draftSlot else { return }
        if field.multiple, let stored = draftSlot.list {
            drafts = stored
            editing = true
        } else if !field.multiple, let stored = draftSlot.text {
            draft = stored
            editing = true
            TaskDraftCaret.moveToEnd()
        }
    }

    private var label: Text {
        let name = Text(verbatim: field.name).foregroundColor(highlight ? DesignColor.red.color : DesignColor.mut.color)
        return field.required ? name + Text(verbatim: " *").foregroundColor(DesignColor.red.color) : name
    }

    private var singleText: String { field.values.first ?? "" }

    private var options: [TaskListVocabulary.EnumValue] { vocabulary.enumValues(field.enumId) }

    private func isSelected(_ option: TaskListVocabulary.EnumValue) -> Bool {
        field.values.contains { $0.caseInsensitiveCompare(option.id) == .orderedSame }
    }

    @ViewBuilder
    private var content: some View {
        if field.multiple, popoverChoices, field.type == .enum {
            Button {
                picking = true
            } label: {
                HStack(spacing: 4) {
                    displayValue
                    MockupIconView(icon: .plus, size: 10).foregroundStyle(DesignColor.dim.color)
                }
                .padding(.horizontal, 6)
                .frame(minHeight: 24)
                .overlay(RoundedRectangle(cornerRadius: DesignRadius.chip).strokeBorder(highlight ? DesignColor.red.color : .clear, lineWidth: DesignLayout.borderWidth))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .popover(isPresented: $picking, arrowEdge: .bottom) {
                ChoicePopover(
                    title: field.name,
                    options: options.map { ChoicePopover.Option(id: $0.id.lowercased(), name: $0.name) },
                    selected: field.values.map { $0.lowercased() }
                ) { set($0) }
            }
        } else if field.multiple {
            Button {
                drafts = field.values
                editing.toggle()
            } label: {
                displayValue
            }
            .buttonStyle(.plain)
        } else {
            switch field.type {
            case .bool:
                Toggle(isOn: Binding(
                    get: { field.values.first == "true" },
                    set: { set([$0 ? "true" : "false"]) }
                )) { EmptyView() }
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
            case .enum:
                Menu {
                    ForEach(options, id: \.id) { option in
                        Button {
                            set([option.id.lowercased()])
                        } label: {
                            if isSelected(option) { Label(option.name, systemImage: "checkmark") } else { Text(option.name) }
                        }
                    }
                    if !field.required, !field.isEmpty {
                        Divider()
                        Button(l.string(.taskDetailClear)) { set([]) }
                    }
                } label: {
                    displayValue
                }
                .menuStyle(.button)
                .buttonStyle(.plain)
                .menuIndicator(.hidden)
            case .date:
                dateEditor
            case .string, .int, .float:
                if editing {
                    TextField(text: $draft) { EmptyView() }
                        .textFieldStyle(.plain)
                        .focused($focused)
                        .padding(.horizontal, 6)
                        .frame(height: 24)
                        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
                        .overlay(RoundedRectangle(cornerRadius: DesignRadius.chip).strokeBorder(invalid || highlight ? DesignColor.red.color : DesignColor.acc.color, lineWidth: DesignLayout.borderWidth))
                        .onSubmit(commitSingle)
                        .onExitCommand(perform: cancelSingle)
                        .onChange(of: focused) { if !focused { commitSingle() } }
                        .onChange(of: draft) { invalid = false }
                        .endsOnClickAway(active: editing, perform: commitSingle)
                        .onAppear { DispatchQueue.main.async { focused = true } }
                } else {
                    Button {
                        draft = singleText
                        invalid = false
                        editing = true
                    } label: {
                        displayValue
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private var dateEditor: some View {
        HStack(spacing: DesignSpacing.gap6) {
            if let date = FieldValueCodec.day(from: singleText) {
                DatePicker("", selection: Binding(
                    get: { date },
                    set: { set([FieldValueCodec.dayString($0)]) }
                ), displayedComponents: .date)
                .labelsHidden()
                .datePickerStyle(.compact)
                .environment(\.locale, l.locale)
                if !field.required {
                    Button { set([]) } label: {
                        MockupIconView(icon: .x, size: 10).foregroundStyle(DesignColor.dim.color)
                            .frame(width: 18, height: 18)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.taskDetailClear))
                }
            } else {
                Button {
                    set([FieldValueCodec.dayString(Date())])
                } label: {
                    displayValue
                }
                .buttonStyle(.plain)
            }
        }
    }

    @ViewBuilder
    private var displayValue: some View {
        let texts = FieldValueCodec.display(field, vocabulary: vocabulary, l: l).filter { !$0.isEmpty }
        if texts.isEmpty {
            Text(l.string(.taskDetailNotSet)).foregroundStyle(DesignColor.dim.color)
        } else if field.type == .enum {
            let names = options
            HStack(spacing: 4) {
                ForEach(Array(zip(field.values, texts).enumerated()), id: \.offset) { _, pair in
                    let index = names.firstIndex { $0.id.caseInsensitiveCompare(pair.0) == .orderedSame }
                    TagLabel(text: pair.1, hex: DesignTag.palette[((index ?? 0) + 1) % DesignTag.palette.count])
                }
            }
        } else {
            Text(verbatim: texts.joined(separator: ", "))
                .lineLimit(2)
                .multilineTextAlignment(.leading)
        }
    }

    @ViewBuilder
    private var multipleEditor: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap6) {
            if field.type == .enum {
                ForEach(options, id: \.id) { option in
                    Toggle(option.name, isOn: Binding(
                        get: { drafts.contains { $0.caseInsensitiveCompare(option.id) == .orderedSame } },
                        set: { on in
                            drafts.removeAll { $0.caseInsensitiveCompare(option.id) == .orderedSame }
                            if on { drafts.append(option.id.lowercased()) }
                        }
                    ))
                    .toggleStyle(.checkbox)
                }
            } else {
                ForEach(drafts.indices, id: \.self) { index in
                    HStack(spacing: DesignSpacing.gap6) {
                        TextField(text: $drafts[index]) { EmptyView() }
                            .textFieldStyle(.roundedBorder)
                        Button { drafts.remove(at: index) } label: {
                            MockupIconView(icon: .x, size: 11).foregroundStyle(DesignColor.dim.color)
                                .frame(width: 18, height: 18)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
                GhostButton(title: l.string(.taskDetailAddValue), icon: .plus) { drafts.append("") }
            }
            HStack(spacing: DesignSpacing.gap8) {
                Button(l.string(.commonSave)) { commitMultiple() }
                    .keyboardShortcut(.return, modifiers: .command)
                Button(l.string(.commonCancel)) { editing = false }
                    .keyboardShortcut(.cancelAction)
                if invalid {
                    Text(l.string(.taskDetailInvalid)).foregroundStyle(DesignColor.red.color).font(.design(DesignTypography.smallSize))
                }
            }
        }
    }

    private func commitSingle() {
        guard editing else { return }
        if draft.trimmingCharacters(in: .whitespacesAndNewlines) == singleText {
            cancelSingle()
            return
        }
        guard let value = FieldValueCodec.normalize(draft, type: field.type) else {
            invalid = true
            focused = true
            return
        }
        editing = false
        let current = field.values.first ?? ""
        guard value != current else { return }
        if value.isEmpty && field.required && current.isEmpty { return }
        set(value.isEmpty ? [] : [value])
    }

    private func cancelSingle() {
        editing = false
        invalid = false
        draft = singleText
    }

    private func commitMultiple() {
        guard let values = FieldValueCodec.normalizeAll(drafts, type: field.type) else {
            invalid = true
            return
        }
        editing = false
        invalid = false
        guard values != field.values else { return }
        set(values)
    }
}

extension FieldRowView {
    init(model: TaskDetailModel, field: TaskFieldValue) {
        self.init(
            field: field, vocabulary: model.vocabulary,
            draftSlot: model.draftSlot("field." + field.fieldId.uuidString),
            set: { model.perform(.setField(field.fieldId, $0)) },
            remove: { model.perform(.removeField(field.fieldId)) })
    }
}

struct ChoicePopover: View {
    struct Option: Hashable, Identifiable {
        var id: String
        var name: String
        var detail: String?
        var keywords = ""
        var glyph: StatusGlyph?
        var tint: Color?
    }

    let title: String
    let options: [Option]
    let selected: [String]
    var single = false
    var placeholder: String?
    let change: ([String]) -> Void
    @Environment(\.localizer) private var l
    @State private var query = ""
    @FocusState private var focused: Bool

    private var shown: [Option] {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return options }
        return options.filter { $0.name.localizedCaseInsensitiveContains(text) || $0.keywords.localizedCaseInsensitiveContains(text) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(verbatim: title)
                .font(.design(DesignTypography.baseSize, weight: DesignTypography.semiboldWeight))
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.top, DesignSpacing.gap10)
            TextField(placeholder ?? l.string(.taskListSortSearch), text: $query)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.vertical, DesignSpacing.gap10)
            DesignDivider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    if shown.isEmpty {
                        Text(l.string(.taskCreateNothingFound)).foregroundStyle(DesignColor.dim.color).padding(DesignSpacing.gap10)
                    }
                    ForEach(shown) { option in
                        let on = selected.contains(option.id)
                        Button {
                            change(single ? [option.id] : (on ? selected.filter { $0 != option.id } : selected + [option.id]))
                        } label: {
                            HStack(spacing: DesignSpacing.gap8) {
                                Image(systemName: single ? (on ? "checkmark" : "circle.fill") : (on ? "checkmark.square.fill" : "square"))
                                    .foregroundStyle(on ? DesignColor.acc.color : single ? .clear : DesignColor.dim.color)
                                if let glyph = option.glyph {
                                    GlyphView(glyph: glyph, color: option.tint ?? DesignColor.mut.color)
                                }
                                Text(verbatim: option.name).lineLimit(1)
                                if let detail = option.detail, !detail.isEmpty {
                                    Text(verbatim: detail)
                                        .font(.design(DesignTypography.smallSize))
                                        .foregroundStyle(DesignColor.dim.color)
                                        .lineLimit(1)
                                }
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
            .frame(height: 220)
        }
        .font(.design(DesignTypography.baseSize))
        .frame(width: single ? 340 : 280)
        .task {
            try? await Task.sleep(for: .milliseconds(150))
            focused = true
        }
    }
}

struct OwnFieldSheet: View {
    let model: TaskDetailModel
    @Binding var presented: Bool
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var type: FieldType = .string
    @State private var multiple = false
    @State private var required = false
    @State private var enumId: UUID?
    @State private var value = ""

    private static let kinds: [FieldType] = [.string, .int, .float, .bool, .date, .enum]

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.taskDetailOwnFieldTitle))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            TextField(l.string(.taskDetailFieldName), text: $name)
                .textFieldStyle(.roundedBorder)
            Picker(l.string(.taskDetailFieldType), selection: $type) {
                ForEach(Self.kinds, id: \.self) { kind in
                    Text(typeTitle(kind)).tag(kind)
                }
            }
            if type == .enum {
                Picker(l.string(.taskDetailEnumPick), selection: $enumId) {
                    Text(l.string(.taskDetailNotSet)).tag(UUID?.none)
                    ForEach(model.vocabulary.enums.keys.sorted { $0.uuidString < $1.uuidString }, id: \.self) { id in
                        Text(model.vocabulary.enumNames[id] ?? id.uuidString).tag(UUID?.some(id))
                    }
                }
            }
            if !multiple, type != .enum, type != .bool {
                TextField(l.string(.taskDetailValue), text: $value)
                    .textFieldStyle(.roundedBorder)
            }
            Toggle(l.string(.taskDetailMultiple), isOn: $multiple)
            Toggle(l.string(.taskDetailRequired), isOn: $required)
            HStack {
                Spacer()
                Button(l.string(.commonCancel)) { presented = false }
                    .keyboardShortcut(.cancelAction)
                Button(l.string(.taskDetailAdd)) { add() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!valid)
            }
        }
        .padding(DesignSpacing.toastPadX)
        .frame(width: 340)
        .font(.design(DesignTypography.baseSize))
    }

    private var valid: Bool {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return false }
        if type == .enum, enumId == nil { return false }
        if value.isEmpty { return true }
        return FieldValueCodec.normalize(value, type: type) != nil
    }

    private func typeTitle(_ kind: FieldType) -> String {
        switch kind {
        case .string: l.string(.taskDetailTypeString)
        case .int: l.string(.taskDetailTypeInt)
        case .float: l.string(.taskDetailTypeFloat)
        case .bool: l.string(.taskDetailTypeBool)
        case .date: l.string(.taskDetailTypeDate)
        case .enum: l.string(.taskDetailTypeEnum)
        }
    }

    private func add() {
        let values = value.isEmpty ? [] : [FieldValueCodec.normalize(value, type: type) ?? value]
        model.perform(.addOwnField(NewOwnFieldInput(
            name: name.trimmingCharacters(in: .whitespaces), type: type, required: required, multiple: multiple,
            enumId: type == .enum ? enumId : nil, values: values)))
        presented = false
    }
}
