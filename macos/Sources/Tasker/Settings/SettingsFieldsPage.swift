import SwiftUI
import TaskerAPI

extension FieldType {
    var label: String { rawValue }
}

private enum FieldEditTarget: Identifiable {
    case new
    case edit(SettingsFieldItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let field): field.id.uuidString
        }
    }
}

struct FieldsSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: FieldEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsFieldItem?
    @State private var checking = false

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageFields, lead: l.string(.settingsFieldsLead),
            add: (l.string(.settingsFieldNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(catalog?.fields ?? []) { field in
                    SettingsRow {
                        HStack(spacing: 6) {
                            Text(field.name)
                            SettingsChip(text: field.type.label + (field.multiple ? "[]" : ""))
                        }
                    } meta: {
                        if let name = catalog?.enumeration(field.enumId)?.name {
                            Text(l.string(.settingsFieldEnumOf, name))
                        }
                    } actions: {
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(field) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { askDelete(field) }
                            .disabled(checking)
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            FieldEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsFieldDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { field in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(field) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { field in
            Text(l.string(.settingsFieldDeleteBody, field.name))
        }
    }

    private func askDelete(_ field: SettingsFieldItem) {
        checking = true
        Task {
            let uses = await model.usage(of: field)
            checking = false
            if uses.isEmpty {
                deleting = field
            } else {
                refusal = SettingsRefusal(subject: l.string(.settingsFieldSubject, field.name), uses: uses)
            }
        }
    }

    private func confirmDelete(_ field: SettingsFieldItem) {
        Task {
            let result = await model.deleteField(field)
            if case .failure(let error) = result {
                if let detail = SettingsModel.isInUse(error) {
                    refusal = SettingsRefusal(subject: l.string(.settingsFieldSubject, field.name), uses: [], detail: detail)
                } else {
                    model.onNotice?(model.message(error))
                }
            }
        }
    }
}

private struct FieldEditSheet: View {
    let model: SettingsModel
    let target: FieldEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var type = FieldType.string
    @State private var multiple = false
    @State private var enumId: UUID?
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false
    @State private var change: PendingFieldChange?

    private var original: SettingsFieldItem? {
        if case .edit(let field) = target { return field }
        return nil
    }

    var body: some View {
        Group {
            if let change {
                FieldChangeStep(model: model, pending: change, error: error, busy: busy, back: { self.change = nil; error = nil }, apply: { request in
                    submit(request, affected: change.page.total)
                }, close: close)
            } else {
                form
            }
        }
        .task {
            if let original {
                name = original.name
                type = original.type
                multiple = original.multiple
                enumId = original.enumId
                if let held = await model.hold(.field(original.id)) {
                    error = held
                    locked = true
                }
            } else {
                enumId = model.catalog?.enums.first?.id
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private var form: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsFieldNewTitle : .settingsFieldEditTitle), error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            SettingsLabeled(title: l.string(.settingsFieldName)) {
                TextField(text: $name) { EmptyView() }.textFieldStyle(.roundedBorder).onSubmit(save)
            }
            SettingsLabeled(title: l.string(.settingsFieldType)) {
                Picker(selection: $type) {
                    ForEach(FieldType.allCases, id: \.self) { Text($0.label).tag($0) }
                } label: { EmptyView() }
                .labelsHidden()
            }
            if type == .enum {
                SettingsLabeled(title: l.string(.settingsFieldEnum)) {
                    Picker(selection: $enumId) {
                        ForEach(model.catalog?.enums ?? []) { Text($0.name).tag(Optional($0.id)) }
                    } label: { EmptyView() }
                    .labelsHidden()
                }
            }
            Toggle(l.string(.settingsFieldMultiple), isOn: $multiple)
        }
    }

    private func validate() -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard SettingsRules.validFieldName(trimmed) else { return l.string(.settingsFieldNameInvalid) }
        let others = (model.catalog?.fields ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmed.lowercased()) { return l.string(.settingsFieldNameTaken) }
        if type == .enum, enumId == nil { return l.string(.settingsFieldEnumRequired) }
        return nil
    }

    private func save() {
        guard !locked, !busy else { return }
        if let problem = validate() {
            error = problem
            return
        }
        error = nil
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard let original else {
            submitNew(trimmed)
            return
        }
        guard SettingsRules.changesValues(old: original, type: type, multiple: multiple, enumId: enumId) else {
            submit(nil, affected: 0)
            return
        }
        busy = true
        Task {
            let page = await model.affectedTasks(of: original)
            busy = false
            guard let page else {
                error = l.string(.settingsFailed, "")
                return
            }
            if page.total == 0 {
                submit(nil, affected: 0)
            } else {
                change = PendingFieldChange(field: original, type: type, multiple: multiple, enumId: enumId, page: page)
            }
        }
    }

    private func submitNew(_ trimmed: String) {
        busy = true
        Task {
            if let failure = await model.createField(name: trimmed, type: type, multiple: multiple, enumId: enumId) {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }

    private func submit(_ request: FieldChangeRequest?, affected: Int) {
        guard let original else { return }
        busy = true
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        Task {
            if let failure = await model.updateField(original, name: trimmed, type: type, multiple: multiple, enumId: enumId, change: request, affected: affected) {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}

struct PendingFieldChange {
    var field: SettingsFieldItem
    var type: FieldType
    var multiple: Bool
    var enumId: UUID?
    var page: SettingsTaskPage
}

private struct FieldChangeStep: View {
    let model: SettingsModel
    let pending: PendingFieldChange
    let error: String?
    let busy: Bool
    let back: () -> Void
    let apply: (FieldChangeRequest) -> Void
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var choice = FieldValueChoice.clear
    @State private var mapping: [String: UUID] = [:]

    private var multipleToSingle: Bool { pending.field.multiple && !pending.multiple }
    private var toEnum: Bool { pending.type == .enum }
    private var newEnum: SettingsEnumItem? { model.catalog?.enumeration(pending.enumId) }
    private var oldEnum: SettingsEnumItem? { model.catalog?.enumeration(pending.field.enumId) }

    private var distinct: [String] {
        var seen = Set<String>()
        var result: [String] = []
        for task in pending.page.items {
            for value in task.values(of: pending.field.id) where seen.insert(value).inserted { result.append(value) }
        }
        return Array(result.sorted { shown($0).localizedStandardCompare(shown($1)) == .orderedAscending }.prefix(100))
    }

    private func shown(_ raw: String) -> String {
        if pending.field.type == .enum, let id = UUID(uuidString: raw), let value = oldEnum?.values.first(where: { $0.id == id }) { return value.name }
        return raw
    }

    var body: some View {
        SettingsSheet(
            title: l.string(.settingsChangeTitle, pending.field.name), width: 520, error: error, saveTitle: l.string(.settingsChangeApply), busy: busy,
            cancel: back, save: submit
        ) {
            Text(l.string(.settingsChangeBody, pending.page.total, refs, "\(pending.field.type.label)\(pending.field.multiple ? "[]" : "")", "\(pending.type.label)\(pending.multiple ? "[]" : "")"))
                .fixedSize(horizontal: false, vertical: true)
            Picker(selection: $choice) {
                Text(l.string(.settingsChangeClear)).tag(FieldValueChoice.clear)
                if toEnum { Text(l.string(.settingsChangeMap)).tag(FieldValueChoice.map) }
                if multipleToSingle { Text(l.string(.settingsChangeFirst)).tag(FieldValueChoice.keepFirst) }
            } label: { EmptyView() }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if choice == .map, let newEnum {
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(distinct, id: \.self) { raw in
                            HStack {
                                Text(shown(raw)).frame(width: 170, alignment: .leading).lineLimit(1)
                                MockupIconView(icon: .back, size: 11).rotationEffect(.degrees(180)).foregroundStyle(DesignColor.dim.color)
                                Picker(selection: Binding(get: { mapping[raw] }, set: { mapping[raw] = $0 })) {
                                    Text(l.string(.settingsChangeByName)).tag(UUID?.none)
                                    ForEach(newEnum.values) { Text($0.name).tag(Optional($0.id)) }
                                } label: { EmptyView() }
                                .labelsHidden()
                            }
                        }
                    }
                }
                .frame(maxHeight: 200)
            }
        }
    }

    private var refs: String {
        let prefixes = model.prefixes
        let shown = pending.page.items.prefix(6).map { SettingsRules.reference($0, prefixes: prefixes) }
        return shown.joined(separator: ", ") + (pending.page.total > 6 ? " …" : "")
    }

    private func submit() {
        var request = FieldChangeRequest()
        if multipleToSingle { request.keepFirst = choice == .keepFirst }
        if choice == .map {
            request.mapping = Dictionary(mapping.map { ($0.key, $0.value.uuidString.lowercased()) }, uniquingKeysWith: { first, _ in first })
        }
        apply(request)
    }
}
