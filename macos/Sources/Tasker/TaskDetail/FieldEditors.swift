import SwiftUI
import TaskerAPI

struct FieldRowView: View {
    let model: TaskDetailModel
    let field: TaskFieldValue
    @Environment(\.localizer) private var l
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
                    if field.source != .type, hover || editing {
                        Button {
                            model.perform(.removeField(field.fieldId))
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
    }

    private var label: Text {
        field.required ? Text(verbatim: field.name) + Text(verbatim: " *").foregroundStyle(DesignColor.red.color) : Text(verbatim: field.name)
    }

    private var singleText: String { field.values.first ?? "" }

    private var options: [TaskListVocabulary.EnumValue] { model.vocabulary.enumValues(field.enumId) }

    private func isSelected(_ option: TaskListVocabulary.EnumValue) -> Bool {
        field.values.contains { $0.caseInsensitiveCompare(option.id) == .orderedSame }
    }

    @ViewBuilder
    private var content: some View {
        if field.multiple {
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
                    set: { model.perform(.setField(field.fieldId, [$0 ? "true" : "false"])) }
                )) { EmptyView() }
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
            case .enum:
                Menu {
                    ForEach(options, id: \.id) { option in
                        Button {
                            model.perform(.setField(field.fieldId, [option.id.lowercased()]))
                        } label: {
                            if isSelected(option) { Label(option.name, systemImage: "checkmark") } else { Text(option.name) }
                        }
                    }
                    if !field.required, !field.isEmpty {
                        Divider()
                        Button(l.string(.taskDetailClear)) { model.perform(.setField(field.fieldId, [])) }
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
                        .overlay(RoundedRectangle(cornerRadius: DesignRadius.chip).strokeBorder(invalid ? DesignColor.red.color : DesignColor.acc.color, lineWidth: DesignLayout.borderWidth))
                        .onSubmit(commitSingle)
                        .onExitCommand(perform: cancelSingle)
                        .onChange(of: focused) { if !focused { commitSingle() } }
                        .onChange(of: draft) { invalid = false }
                        .onAppear { focused = true }
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
                    set: { model.perform(.setField(field.fieldId, [FieldValueCodec.dayString($0)])) }
                ), displayedComponents: .date)
                .labelsHidden()
                .datePickerStyle(.compact)
                .environment(\.locale, l.locale)
                if !field.required {
                    Button { model.perform(.setField(field.fieldId, [])) } label: {
                        MockupIconView(icon: .x, size: 10).foregroundStyle(DesignColor.dim.color)
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.taskDetailClear))
                }
            } else {
                Button {
                    model.perform(.setField(field.fieldId, [FieldValueCodec.dayString(Date())]))
                } label: {
                    displayValue
                }
                .buttonStyle(.plain)
            }
        }
    }

    @ViewBuilder
    private var displayValue: some View {
        let texts = FieldValueCodec.display(field, vocabulary: model.vocabulary, l: l).filter { !$0.isEmpty }
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
        guard let value = FieldValueCodec.normalize(draft, type: field.type) else {
            invalid = true
            focused = true
            return
        }
        editing = false
        let current = field.values.first ?? ""
        guard value != current else { return }
        if value.isEmpty && field.required && current.isEmpty { return }
        model.perform(.setField(field.fieldId, value.isEmpty ? [] : [value]))
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
        model.perform(.setField(field.fieldId, values))
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
