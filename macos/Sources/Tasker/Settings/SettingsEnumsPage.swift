import SwiftUI
import TaskerAPI
import UniformTypeIdentifiers

private enum EnumEditTarget: Identifiable {
    case new
    case edit(SettingsEnumItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct EnumsSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: EnumEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsEnumItem?

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageEnums, lead: l.string(.settingsEnumsLead),
            add: (l.string(.settingsEnumNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(catalog?.enums ?? []) { item in
                    let fields = catalog?.fields(of: item.id) ?? []
                    SettingsRow {
                        HStack(spacing: 8) {
                            Button(item.name) { editing = .edit(item) }.buttonStyle(.plain)
                            Text(l.string(.settingsEnumValuesCount, item.values.count))
                                .font(.design(DesignTypography.smallSize))
                                .foregroundStyle(DesignColor.dim.color)
                        }
                    } meta: {
                        VStack(alignment: .leading, spacing: 3) {
                            BoardFlowLayout(spacing: 4) {
                                ForEach(item.values.prefix(8)) { SettingsChip(text: $0.name) }
                                if item.values.count > 8 {
                                    Text(verbatim: "+\(item.values.count - 8)").foregroundStyle(DesignColor.dim.color)
                                }
                            }
                            if !fields.isEmpty {
                                Text(l.string(.settingsEnumFields, fields.map(\.name).joined(separator: ", ")))
                            }
                        }
                    } actions: {
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { deleting = item }
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            EnumEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsEnumDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsEnumDeleteBody, item.name))
        }
    }

    private func confirmDelete(_ item: SettingsEnumItem) {
        Task {
            if case .failure(let error) = await model.deleteEnum(item) {
                if let detail = SettingsModel.inUseRefusal(error) {
                    refusal = SettingsRefusal(refusal: detail)
                } else {
                    model.onFailure?(model.message(error))
                }
            }
        }
    }
}

private struct PendingRemoval {
    var gone: [SettingsEnumValue]
    var tasks: Int
    var columns: Int
}

private struct EnumEditSheet: View {
    let model: SettingsModel
    let target: EnumEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var values: [EnumValueDraft] = []
    @State private var query = ""
    @State private var newValue = ""
    @State private var usage = EnumUsage()
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false
    @State private var removal: PendingRemoval?
    @State private var removalMode = RemovalMode.clear
    @State private var replacement: UUID?

    private enum RemovalMode { case clear, reassign }

    private var original: SettingsEnumItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    private var filtering: Bool { !query.trimmingCharacters(in: .whitespaces).isEmpty }

    var body: some View {
        Group {
            if let removal { removalStep(removal) } else { editor }
        }
        .task {
            guard let original else { return }
            name = original.name
            values = original.values.map { EnumValueDraft(serverId: $0.id, name: $0.name) }
            if let held = await model.hold(.enumeration(original.id)) {
                error = held
                locked = true
            }
            usage = await model.enumUsage(original)
        }
        .onDisappear { model.dropSoon() }
    }

    private var editor: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsEnumNewTitle : .settingsEnumEditTitle), width: 560, error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            TextField(l.string(.settingsEnumName), text: $name).textFieldStyle(.roundedBorder)
            HStack(spacing: 8) {
                Text(l.string(.settingsEnumValues).uppercased())
                    .font(.design(DesignTypography.captionSize, weight: DesignTypography.semiboldWeight))
                    .foregroundStyle(DesignColor.mut.color)
                Text(verbatim: "\(values.count)").foregroundStyle(DesignColor.dim.color)
                Spacer()
                if values.count > 6 {
                    TextField(l.string(.settingsEnumSearch), text: $query).textFieldStyle(.roundedBorder).frame(width: 180)
                }
            }
            list
            HStack(spacing: 6) {
                MockupIconView(icon: .plus, size: 12).foregroundStyle(DesignColor.mut.color)
                TextField(l.string(.settingsEnumAddPlaceholder), text: $newValue)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(addTyped)
                    .onPasteCommand(of: [.plainText]) { providers in paste(providers) }
            }
        }
    }

    private var visible: [Int] {
        let text = query.trimmingCharacters(in: .whitespaces).lowercased()
        return values.indices.filter { text.isEmpty || values[$0].name.lowercased().contains(text) }
    }

    private var list: some View {
        List {
            if visible.isEmpty {
                Text(l.string(filtering ? .settingsEnumNoMatch : .settingsEnumEmpty)).foregroundStyle(DesignColor.dim.color)
            }
            ForEach(visible, id: \.self) { index in
                row(index)
            }
            .onMove { source, destination in
                guard !filtering else { return }
                values.move(fromOffsets: source, toOffset: destination)
            }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .frame(height: 260)
        .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }

    private func row(_ index: Int) -> some View {
        let value = values[index]
        let count = value.serverId.map { usage.count($0) } ?? 0
        return HStack(spacing: 8) {
            Text(verbatim: "⋮⋮").foregroundStyle(DesignColor.dim.color).opacity(filtering ? 0 : 1)
            Text(verbatim: String(index + 1)).font(.design(DesignTypography.smallSize, mono: true)).foregroundStyle(DesignColor.dim.color).frame(width: 26, alignment: .trailing)
            TextField(text: Binding(get: { values.indices.contains(index) ? values[index].name : "" }, set: { if values.indices.contains(index) { values[index].name = $0 } })) { EmptyView() }
                .textFieldStyle(.roundedBorder)
            Text(count > 0 ? l.string(.countTasks, count) : "")
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.dim.color)
                .frame(width: 80, alignment: .trailing)
            SettingsIconButton(icon: .up, help: l.string(.settingsEnumUp)) { values = SettingsRules.moved(values, from: index, to: index - 1) }
                .disabled(index == 0 || filtering)
            SettingsIconButton(icon: .down, help: l.string(.settingsEnumDown)) { values = SettingsRules.moved(values, from: index, to: index + 1) }
                .disabled(index == values.count - 1 || filtering)
            SettingsIconButton(icon: .x, help: l.string(.settingsEnumRemove)) { values.remove(at: index) }
        }
    }

    private func addTyped() {
        add(SettingsRules.valueNames(from: newValue))
        newValue = ""
    }

    private func paste(_ providers: [NSItemProvider]) {
        for provider in providers {
            _ = provider.loadObject(ofClass: NSString.self) { object, _ in
                guard let text = object as? String else { return }
                Task { @MainActor in
                    let combined = SettingsRules.valueNames(from: newValue + text)
                    if text.contains(where: \.isNewline) {
                        add(combined)
                        newValue = ""
                    } else {
                        newValue += text
                    }
                }
            }
        }
    }

    private func add(_ names: [String]) {
        guard !names.isEmpty else { return }
        values.append(contentsOf: names.map { EnumValueDraft(name: $0) })
        if names.count > 1 { model.onNotice?(l.string(.settingsEnumAdded, String(names.count))) }
    }

    private func validate() -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return l.string(.settingsEnumNameRequired) }
        let names = values.map { $0.name.trimmingCharacters(in: .whitespaces) }
        if values.isEmpty { return l.string(.settingsEnumNeedValue) }
        if names.contains(where: \.isEmpty) { return l.string(.settingsEnumValueEmpty) }
        if let duplicate = SettingsRules.duplicate(in: names) { return l.string(.settingsEnumValueRepeats, duplicate) }
        let others = (model.catalog?.enums ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmed.lowercased()) { return l.string(.settingsEnumNameTaken) }
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
        let cleaned = values.map { EnumValueDraft(key: $0.key, serverId: $0.serverId, name: $0.name.trimmingCharacters(in: .whitespaces)) }
        guard let original else {
            busy = true
            Task {
                if let failure = await model.createEnum(name: trimmed, values: cleaned.map(\.name)) {
                    error = failure
                    busy = false
                } else {
                    close()
                }
            }
            return
        }
        let kept = Set(cleaned.compactMap(\.serverId))
        let gone = original.values.filter { !kept.contains($0.id) }
        let used = gone.filter { usage.count($0.id) > 0 || usage.columns[$0.id, default: 0] > 0 }
        if !gone.isEmpty, !used.isEmpty || !usage.complete {
            let pending = PendingRemoval(
                gone: used.isEmpty ? gone : used, tasks: used.reduce(0) { $0 + usage.count($1.id) }, columns: used.reduce(0) { $0 + usage.columns[$1.id, default: 0] })
            replacement = cleaned.first(where: { $0.serverId != nil })?.serverId
            removalMode = .clear
            removal = pending
            return
        }
        commit(original, trimmed, cleaned, nil)
    }

    private func commit(_ original: SettingsEnumItem, _ trimmed: String, _ cleaned: [EnumValueDraft], _ choice: EnumRemovalChoice?) {
        busy = true
        Task {
            if let failure = await model.updateEnum(original, name: trimmed, values: cleaned, removal: choice) {
                error = failure
                busy = false
                removal = nil
            } else {
                close()
            }
        }
    }

    private func removalStep(_ pending: PendingRemoval) -> some View {
        let candidates = values.filter { draft in draft.serverId != nil && !pending.gone.contains { $0.id == draft.serverId } }
        return SettingsSheet(
            title: l.string(.settingsRemovalTitle), width: 500, error: error, saveTitle: l.string(.commonSave), busy: busy,
            cancel: { removal = nil }, save: {
                guard let original else { return }
                let choice: EnumRemovalChoice = removalMode == .reassign && replacement != nil ? .reassign(replacement!) : .clear
                commit(
                    original, name.trimmingCharacters(in: .whitespaces),
                    values.map { EnumValueDraft(key: $0.key, serverId: $0.serverId, name: $0.name.trimmingCharacters(in: .whitespaces)) }, choice)
            }
        ) {
            Text(l.string(.settingsRemovalBody, pending.gone.map { "«\($0.name)»" }.joined(separator: ", "), pending.tasks, String(pending.columns)))
                .fixedSize(horizontal: false, vertical: true)
            Picker(selection: $removalMode) {
                Text(l.string(.settingsRemovalClear)).tag(RemovalMode.clear)
                Text(l.string(.settingsRemovalReplace)).tag(RemovalMode.reassign)
            } label: { EmptyView() }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if removalMode == .reassign {
                if candidates.isEmpty {
                    Text(l.string(.settingsRemovalNoTarget)).foregroundStyle(DesignColor.red.color)
                } else {
                    Picker(selection: $replacement) {
                        ForEach(candidates) { Text($0.name).tag($0.serverId) }
                    } label: { EmptyView() }
                    .labelsHidden()
                }
            }
        }
    }
}
