import SwiftUI
import TaskerAPI

private enum TypeEditTarget: Identifiable {
    case new
    case edit(SettingsTypeItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct TypesSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: TypeEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsTypeItem?
    @State private var checking = false

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageTypes, lead: l.string(.settingsTypesLead),
            add: (l.string(.settingsTypeNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(catalog?.types ?? []) { item in
                    SettingsRow {
                        Button(item.name) { editing = .edit(item) }.buttonStyle(.plain)
                    } meta: {
                        VStack(alignment: .leading, spacing: 3) {
                            if !item.description.isEmpty { Text(item.description).lineLimit(2) }
                            Text(summary(item, catalog))
                        }
                    } actions: {
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { askDelete(item) }
                            .disabled(checking)
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            TypeEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsTypeDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsTypeDeleteBody, item.name))
        }
    }

    private func summary(_ item: SettingsTypeItem, _ catalog: SettingsCatalog?) -> String {
        let set = catalog?.statusSet(item.statusSetId)?.name ?? "—"
        let fields = item.fields.compactMap { ref in catalog?.field(ref.fieldId).map { $0.name + (ref.required ? "*" : "") } }
        if fields.isEmpty { return l.string(.settingsTypeSummaryNoFields, set) }
        return l.string(.settingsTypeSummary, set, fields.joined(separator: ", "))
    }

    private func askDelete(_ item: SettingsTypeItem) {
        checking = true
        Task {
            let uses = await model.taskTypeUsage(of: item)
            checking = false
            if uses.isEmpty {
                deleting = item
            } else {
                refusal = SettingsRefusal(subject: l.string(.settingsTypeSubject, item.name), uses: uses)
            }
        }
    }

    private func confirmDelete(_ item: SettingsTypeItem) {
        Task {
            if case .failure(let error) = await model.deleteTaskType(item) {
                if let detail = SettingsModel.isInUse(error) {
                    refusal = SettingsRefusal(subject: l.string(.settingsTypeSubject, item.name), uses: [], detail: detail)
                } else {
                    model.onNotice?(model.message(error))
                }
            }
        }
    }
}

private struct PendingFieldRemoval {
    var names: [String]
    var tasks: Int
}

private struct TypeEditSheet: View {
    let model: SettingsModel
    let target: TypeEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var text = ""
    @State private var setId: UUID?
    @State private var fields: [SettingsTypeField] = []
    @State private var pickingSet = false
    @State private var pickingField = false
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false
    @State private var removal: PendingFieldRemoval?
    @State private var keep = true

    private var original: SettingsTypeItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    private var catalog: SettingsCatalog? { model.catalog }

    var body: some View {
        Group {
            if let removal { removalStep(removal) } else { editor }
        }
        .task {
            if let original {
                name = original.name
                text = original.description
                setId = original.statusSetId
                fields = original.fields
                if let held = await model.hold(.taskType(original.id)) {
                    error = held
                    locked = true
                }
            } else {
                setId = catalog?.statusSets.first?.id
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private var editor: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsTypeNewTitle : .settingsTypeEditTitle), width: 680, error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            TextField(l.string(.settingsTypeName), text: $name).textFieldStyle(.roundedBorder)
            TextEditor(text: $text)
                .font(.design(DesignTypography.baseSize))
                .frame(height: 48)
                .padding(4)
                .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
                .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
                .overlay(alignment: .topLeading) {
                    if text.isEmpty {
                        Text(l.string(.settingsTypeDescription)).foregroundStyle(DesignColor.dim.color).padding(.horizontal, 9).padding(.vertical, 6).allowsHitTesting(false)
                    }
                }
            SettingsLabeled(title: l.string(.settingsTypeStatusSet)) { setCard }
            SettingsLabeled(title: l.string(.settingsTypeFields)) { fieldList }
            Button {
                addField()
            } label: {
                HStack(spacing: 4) {
                    MockupIconView(icon: .plus, size: 11)
                    Text(l.string(.settingsTypeAddField))
                }
            }
            .popover(isPresented: $pickingField, arrowEdge: .bottom) { fieldPicker }
        }
    }

    private var setCard: some View {
        let set = catalog?.statusSet(setId)
        return VStack(alignment: .leading, spacing: DesignSpacing.gap6) {
            HStack {
                Text(set?.name ?? "—").font(.designEmphasis(DesignTypography.baseSize))
                Spacer()
                Button(l.string(.settingsTypeChangeSet)) { pickingSet = true }
                    .popover(isPresented: $pickingSet, arrowEdge: .bottom) { setPicker }
            }
            if let set, let catalog {
                BoardFlowLayout(spacing: 6) {
                    ForEach(Array(set.statusIds.enumerated()), id: \.offset) { index, id in
                        if index > 0 { Text(verbatim: "→").foregroundStyle(DesignColor.dim.color) }
                        if let status = catalog.status(id) {
                            HStack(spacing: 4) {
                                GlyphView(glyph: catalog.glyph(of: status, in: set), color: Color(designHex: status.color), size: 14)
                                Text(status.name)
                            }
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(DesignSpacing.gap10)
        .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.acc.color, lineWidth: DesignLayout.borderWidth))
    }

    private var setPicker: some View {
        let options = (catalog?.statusSets ?? []).map { set in
            let names = set.statusIds.compactMap { catalog?.status($0)?.name }
            return ChoicePopover.Option(id: set.id.uuidString, name: set.name, detail: names.joined(separator: " → "), keywords: names.joined(separator: " "))
        }
        return ChoicePopover(
            title: l.string(.settingsTypeStatusSet), options: options, selected: setId.map { [$0.uuidString] } ?? [], single: true,
            placeholder: l.string(.settingsTypeSetSearch)
        ) { chosen in
            if let id = chosen.first.flatMap(UUID.init(uuidString:)) { setId = id }
            pickingSet = false
        }
    }

    private var fieldList: some View {
        VStack(spacing: 0) {
            if fields.isEmpty {
                Text(l.string(.settingsTypeNoFields)).foregroundStyle(DesignColor.dim.color).padding(DesignSpacing.gap10).frame(maxWidth: .infinity, alignment: .leading)
            }
            ForEach(Array(fields.enumerated()), id: \.element.fieldId) { index, ref in
                fieldRow(index, ref)
            }
        }
        .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }

    private func fieldRow(_ index: Int, _ ref: SettingsTypeField) -> some View {
        let field = catalog?.field(ref.fieldId)
        return HStack(spacing: 10) {
            Text(field?.name ?? ref.fieldId.uuidString).font(.designEmphasis(DesignTypography.baseSize))
            if let field {
                Text(typeText(field)).foregroundStyle(DesignColor.mut.color)
                if field.multiple { SettingsChip(text: l.string(.settingsTypeMultiple)) }
            }
            Spacer(minLength: 0)
            Toggle(l.string(.settingsTypeRequired), isOn: Binding(
                get: { fields.indices.contains(index) ? fields[index].required : false },
                set: { if fields.indices.contains(index) { fields[index].required = $0 } }))
                .toggleStyle(.switch)
                .controlSize(.small)
            SettingsIconButton(icon: .x, help: l.string(.settingsTypeRemoveField)) { fields.remove(at: index) }
        }
        .padding(.horizontal, DesignSpacing.gap10)
        .padding(.vertical, 6)
        .overlay(alignment: .bottom) { if index < fields.count - 1 { DesignDivider() } }
    }

    private func typeText(_ field: SettingsFieldItem) -> String {
        if field.type == .enum, let name = catalog?.enumeration(field.enumId)?.name { return l.string(.settingsFieldEnumOf, name) }
        return field.type.label
    }

    private var freeFields: [SettingsFieldItem] {
        let used = Set(fields.map(\.fieldId))
        return (catalog?.fields ?? []).filter { !used.contains($0.id) }
    }

    private func addField() {
        if freeFields.isEmpty {
            error = l.string(.settingsTypeAllFieldsAdded)
        } else {
            error = nil
            pickingField = true
        }
    }

    private var fieldPicker: some View {
        let options = freeFields.map { ChoicePopover.Option(id: $0.id.uuidString, name: $0.name, detail: typeText($0)) }
        return ChoicePopover(title: l.string(.settingsTypeAddField), options: options, selected: [], single: true, placeholder: l.string(.settingsTypeFieldSearch)) { chosen in
            if let id = chosen.first.flatMap(UUID.init(uuidString:)) { fields.append(SettingsTypeField(fieldId: id, required: false)) }
            pickingField = false
        }
    }

    private func validate() -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return l.string(.settingsTypeNameRequired) }
        if setId == nil { return l.string(.settingsTypeNeedSet) }
        let others = (catalog?.types ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmed.lowercased()) { return l.string(.settingsTypeNameTaken) }
        return nil
    }

    private func save() {
        guard !locked, !busy else { return }
        if let problem = validate() {
            error = problem
            return
        }
        error = nil
        guard let original else {
            commit(nil, nil)
            return
        }
        let removed = SettingsRules.removedFields(old: original.fields, new: fields)
        guard !removed.isEmpty else {
            commit(original, nil)
            return
        }
        busy = true
        Task {
            let holders = await model.holdersOfRemovedFields(in: original, fields: removed)
            busy = false
            if let holders, holders.tasks == 0 {
                commit(original, nil)
            } else {
                let names = holders?.names ?? removed.compactMap { catalog?.field($0)?.name }
                removal = PendingFieldRemoval(names: names, tasks: holders?.tasks ?? 0)
                keep = true
            }
        }
    }

    private func commit(_ original: SettingsTypeItem?, _ choice: RemovedFieldChoice?) {
        guard let setId else { return }
        busy = true
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        Task {
            let failure: String?
            if let original {
                failure = await model.updateTaskType(original, name: trimmed, description: text, statusSetId: setId, fields: fields, removal: choice)
            } else {
                failure = await model.createTaskType(name: trimmed, description: text, statusSetId: setId, fields: fields)
            }
            if let failure {
                error = failure
                busy = false
                removal = nil
            } else {
                close()
            }
        }
    }

    private func removalStep(_ pending: PendingFieldRemoval) -> some View {
        SettingsSheet(
            title: l.string(.settingsTypeRemovalTitle), width: 500, error: error, busy: busy, cancel: { removal = nil; error = nil },
            save: { commit(original, keep ? .keep : .clear) }
        ) {
            Text(l.string(.settingsTypeRemovalBody, original?.name ?? name, pending.names.joined(separator: ", ")))
                .fixedSize(horizontal: false, vertical: true)
            Picker(selection: $keep) {
                Text(l.string(.settingsTypeRemovalKeep)).tag(true)
                Text(l.string(.settingsTypeRemovalClear)).tag(false)
            } label: { EmptyView() }
            .pickerStyle(.radioGroup)
            .labelsHidden()
        }
    }
}
