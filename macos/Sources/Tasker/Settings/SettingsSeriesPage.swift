import SwiftUI
import TaskerAPI

private enum SeriesEditTarget: Identifiable {
    case new
    case edit(SettingsSeriesItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct SeriesSettingsPage: View {
    let model: SettingsModel
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @State private var editing: SeriesEditTarget?
    @State private var deleting: SettingsSeriesItem?
    @State private var failure: String?
    @State private var resolving: UUID?

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageSeries, lead: l.string(.settingsSeriesLead),
            add: (l.string(.settingsSeriesNew), { editing = .new })
        ) {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                health(catalog?.health ?? SettingsHealth())
                if let failure { Text(failure).foregroundStyle(DesignColor.red.color) }
                VStack(spacing: 0) {
                    ForEach(catalog?.series ?? []) { item in
                        let stat = model.stats[item.id]
                        SettingsRow {
                            Button {
                                session.showSeries(item.id)
                            } label: {
                                HStack(spacing: 8) {
                                    Text(item.prefix).font(.design(DesignTypography.baseSize, weight: DesignTypography.semiboldWeight, mono: true)).foregroundStyle(DesignColor.acc.color)
                                    Text(item.name)
                                }
                            }
                            .buttonStyle(.plain)
                            .help(l.string(.settingsSeriesOpen))
                        } meta: {
                            Text(l.string(.settingsSeriesMeta, stat?.count ?? 0, String(stat?.next ?? 1)))
                        } actions: {
                            SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                            SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { deleting = item }
                        }
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            SeriesEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: Binding(get: { resolving.map(ResolveTarget.init) }, set: { resolving = $0?.id })) { target in
            if let client = live.client {
                SeriesResolveHost(client: client, project: model.project, series: target.id) {
                    resolving = nil
                    model.load()
                }
            }
        }
        .confirmationDialog(
            l.string(.settingsSeriesDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsSeriesDeleteBody, item.prefix, model.stats[item.id]?.count ?? 0))
        }
    }

    private struct ResolveTarget: Identifiable {
        let id: UUID
    }

    @ViewBuilder
    private func health(_ value: SettingsHealth) -> some View {
        if value.isClean {
            SettingsCard {
                HStack(spacing: 6) {
                    MockupIconView(icon: .check, size: 13)
                    Text(l.string(.settingsSeriesHealthClean))
                }
                .foregroundStyle(DesignColor.green.color)
            }
        } else {
            SettingsCard(tint: DesignColor.amber.color.opacity(0.5)) {
                if !value.numberConflicts.isEmpty {
                    HStack(spacing: 6) {
                        MockupIconView(icon: .warn, size: 13)
                        Text(l.string(.settingsSeriesConflicts, String(value.numberConflicts.count)))
                    }
                    .font(.designEmphasis(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.amber.color)
                    ForEach(Array(value.numberConflicts.enumerated()), id: \.offset) { _, conflict in
                        let prefix = model.prefixes[conflict.seriesId] ?? ""
                        HStack {
                            Text(verbatim: "\(prefix)-\(conflict.number)").font(.design(DesignTypography.baseSize, mono: true))
                            Text(l.string(.countTasks, conflict.taskIds.count)).foregroundStyle(DesignColor.mut.color)
                            Spacer()
                        }
                    }
                    ForEach(Array(Set(value.numberConflicts.map(\.seriesId))).sorted { (model.prefixes[$0] ?? "") < (model.prefixes[$1] ?? "") }, id: \.self) { id in
                        Button(l.string(.settingsSeriesResolve, model.prefixes[id] ?? "")) { resolving = id }
                    }
                }
                if !value.prefixConflicts.isEmpty {
                    Text(l.string(.settingsSeriesPrefixConflicts, value.prefixConflicts.joined(separator: ", "))).foregroundStyle(DesignColor.amber.color)
                }
                if value.invalidTasks > 0 {
                    Text(l.string(.settingsSeriesInvalidTasks, String(value.invalidTasks))).foregroundStyle(DesignColor.amber.color)
                }
                if value.unreadableFiles > 0 {
                    Text(l.string(.settingsSeriesUnreadable, String(value.unreadableFiles))).foregroundStyle(DesignColor.amber.color)
                }
            }
        }
    }

    private func confirmDelete(_ item: SettingsSeriesItem) {
        Task {
            if let message = await model.deleteSeries(item) { failure = message } else { failure = nil }
        }
    }
}

private struct SeriesResolveHost: View {
    let client: TaskerAPIClient
    let project: UUID
    let series: UUID
    let done: () -> Void
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var model: SeriesPageModel?

    var body: some View {
        Group {
            if let model {
                SeriesResolveSheet(model: model)
                    .onChange(of: model.resolve) { _, state in
                        if state == .idle { done() }
                    }
            } else {
                ProgressView().padding(40)
            }
        }
        .task {
            let value = SeriesPageModel(project: project, series: series, source: GraphQLSeriesSource(client: client))
            value.localizer = l
            value.onNotice = { [session] in session.notice($0) }
            await value.reload()
            model = value
            value.prepareResolve()
        }
    }
}

private struct SeriesEditSheet: View {
    let model: SettingsModel
    let target: SeriesEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var prefix = ""
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false

    private var original: SettingsSeriesItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    var body: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsSeriesNewTitle : .seriesEditTitle), error: error, canSave: !locked, busy: busy, cancel: close, save: save
        ) {
            SettingsLabeled(title: l.string(.seriesName)) { TextField(text: $name) { EmptyView() }.textFieldStyle(.roundedBorder) }
            SettingsLabeled(title: l.string(.seriesPrefix)) {
                TextField(text: $prefix) { EmptyView() }.textFieldStyle(.roundedBorder).font(.design(DesignTypography.baseSize, mono: true)).onSubmit(save)
            }
        }
        .task {
            guard let original else { return }
            name = original.name
            prefix = original.prefix
            if let held = await model.hold(.series(original.id)) {
                error = held
                locked = true
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private func save() {
        guard !locked, !busy else { return }
        let trimmedPrefix = prefix.trimmingCharacters(in: .whitespaces)
        let others = (model.catalog?.series ?? []).filter { $0.id != original?.id }.map(\.prefix)
        switch SeriesForm.validate(name: name, prefix: trimmedPrefix, others: others) {
        case .nameRequired: error = l.string(.seriesNameRequired); return
        case .prefixInvalid: error = l.string(.seriesPrefixInvalid); return
        case .prefixTaken(let value): error = l.string(.seriesPrefixTaken, value); return
        case nil: break
        }
        error = nil
        busy = true
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        Task {
            let failure: String?
            if let original {
                failure = await model.updateSeries(original, name: trimmedName, prefix: trimmedPrefix)
            } else {
                failure = await model.createSeries(name: trimmedName, prefix: trimmedPrefix)
            }
            if let failure {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}
