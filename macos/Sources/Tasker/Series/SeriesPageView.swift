import SwiftUI
import TaskerAPI

struct SeriesPageHeader: View {
    let client: TaskerAPIClient
    let project: UUID
    let series: UUID
    let onRenumbered: () -> Void
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var model: SeriesPageModel?

    var body: some View {
        VStack(spacing: 0) {
            if let model {
                SeriesHeaderBar(model: model) { session.showSeries($0) }
                    .sheet(isPresented: Binding(get: { model.editing }, set: { model.editing = $0 })) {
                        SeriesEditSheet(model: model)
                    }
                    .sheet(isPresented: Binding(get: { model.resolve != .idle }, set: { if !$0 { model.cancelResolve() } })) {
                        SeriesResolveSheet(model: model)
                    }
            }
        }
        .task(id: series) {
            let value = SeriesPageModel(project: project, series: series, source: GraphQLSeriesSource(client: client))
            value.localizer = l
            value.onNotice = { [session] in session.notice($0) }
            value.onRenumbered = onRenumbered
            model = value
            value.load()
        }
        .onChange(of: live.revision) { model?.load() }
        .onChange(of: l.language) { model?.localizer = l }
        .onChange(of: model?.info?.prefix, initial: true) { session.seriesPrefix = model?.info?.prefix }
        .onDisappear { model?.stop() }
    }
}

private struct SeriesHeaderBar: View {
    let model: SeriesPageModel
    let choose: (UUID) -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: 14) {
            switch model.phase {
            case .loading:
                Text(l.string(.taskListLoading))
                    .foregroundStyle(DesignColor.mut.color)
            case .missing:
                Text(l.string(.seriesMissing))
                    .foregroundStyle(DesignColor.mut.color)
            case .failed(let message):
                Text(l.string(.seriesLoadFailed, message))
                    .foregroundStyle(DesignColor.mut.color)
                    .lineLimit(2)
            case .ready:
                if let info = model.info {
                    content(info)
                }
            }
            Spacer(minLength: 0)
            if model.phase != .loading { switcher }
            if model.phase == .ready {
                GhostButton(title: l.string(.seriesEdit), icon: .pen) { model.editing = true }
            }
        }
        .padding(.horizontal, DesignSpacing.pagePadX)
        .padding(.vertical, 14)
        .overlay(alignment: .bottom) { DesignDivider() }
    }

    private func content(_ info: SeriesInfo) -> some View {
        HStack(spacing: 14) {
            Text(info.prefix)
                .font(.design(20, weight: DesignTypography.semiboldWeight, mono: true))
                .foregroundStyle(DesignColor.acc.color)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(DesignColor.accbg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
            VStack(alignment: .leading, spacing: 2) {
                Text(info.name)
                    .font(.design(15, weight: DesignTypography.semiboldWeight))
                    .lineLimit(1)
                meta(info)
            }
        }
    }

    private func meta(_ info: SeriesInfo) -> some View {
        HStack(spacing: 0) {
            Text(l.string(.countTasks, model.stats?.count ?? 0))
            Text(verbatim: " · ")
            Text(l.string(.seriesNextNumber, "\(info.prefix)-\(model.nextNumber)"))
            if model.conflictCount > 0 {
                Text(verbatim: " · ")
                Button {
                    model.prepareResolve()
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .warn, size: 12)
                        Text(l.string(.seriesConflicts, l.number(Double(model.conflictCount))))
                            .underline()
                    }
                    .foregroundStyle(DesignColor.amber.color)
                }
                .buttonStyle(.plain)
                .help(l.string(.seriesResolve))
            }
        }
        .font(.design(12.5))
        .foregroundStyle(DesignColor.dim.color)
    }

    private var switcher: some View {
        HStack(spacing: 2) {
            ForEach(model.all) { item in
                Button {
                    choose(item.id)
                } label: {
                    Text(item.prefix)
                        .font(.design(DesignTypography.smallSize, mono: true))
                        .foregroundStyle(item.id == model.series ? DesignColor.tx.color : DesignColor.mut.color)
                        .padding(.horizontal, 9)
                        .padding(.vertical, 3)
                        .background(item.id == model.series ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(l.string(.seriesSwitch, item.name))
                .accessibilityAddTraits(item.id == model.series ? .isSelected : [])
            }
        }
        .padding(2)
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.control + 1).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }
}

private struct SeriesEditSheet: View {
    let model: SeriesPageModel
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var prefix = ""
    @State private var error: String?
    @FocusState private var focus: Field?

    private enum Field { case name, prefix }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.seriesEditTitle))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            VStack(alignment: .leading, spacing: 4) {
                Text(l.string(.seriesName)).foregroundStyle(DesignColor.mut.color)
                TextField(text: $name) { EmptyView() }
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .name)
                    .onSubmit { focus = .prefix }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(l.string(.seriesPrefix)).foregroundStyle(DesignColor.mut.color)
                TextField(text: $prefix) { EmptyView() }
                    .textFieldStyle(.roundedBorder)
                    .font(.design(DesignTypography.baseSize, mono: true))
                    .focused($focus, equals: .prefix)
                    .onSubmit(save)
            }
            if let error {
                Text(error)
                    .foregroundStyle(DesignColor.red.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                Button(l.string(.commonCancel)) { model.editing = false }
                    .keyboardShortcut(.cancelAction)
                Button(l.string(.commonSave), action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.saving)
            }
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: 420)
        .font(.design(DesignTypography.baseSize))
        .background(DesignColor.panel.color)
        .onAppear {
            name = model.info?.name ?? ""
            prefix = model.info?.prefix ?? ""
            focus = .name
        }
    }

    private func save() {
        let trimmed = prefix.trimmingCharacters(in: .whitespaces)
        if let problem = model.validate(name: name, prefix: trimmed) {
            error = problem
            return
        }
        Task {
            if let failure = await model.save(name: name, prefix: trimmed) {
                error = failure
            } else {
                model.editing = false
            }
        }
    }
}

struct SeriesResolveSheet: View {
    let model: SeriesPageModel
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.seriesResolveTitle))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            content
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: 520)
        .font(.design(DesignTypography.baseSize))
        .background(DesignColor.panel.color)
    }

    @ViewBuilder
    private var content: some View {
        switch model.resolve {
        case .idle, .preparing:
            HStack(spacing: DesignSpacing.gap8) {
                ProgressView().controlSize(.small)
                Text(l.string(.seriesResolveLoading)).foregroundStyle(DesignColor.mut.color)
            }
            footer(apply: nil)
        case .ready(let plan, let stale):
            if stale {
                Text(l.string(.seriesResolveStale))
                    .foregroundStyle(DesignColor.amber.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text(l.string(.seriesResolveHint)).foregroundStyle(DesignColor.mut.color)
            ScrollView {
                VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                    ForEach(plan.groups) { group in groupView(group) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 280)
            footer(apply: true)
        case .applying:
            HStack(spacing: DesignSpacing.gap8) {
                ProgressView().controlSize(.small)
                Text(l.string(.seriesResolveApplying)).foregroundStyle(DesignColor.mut.color)
            }
        case .failed(let message):
            Text(message)
                .foregroundStyle(DesignColor.red.color)
                .fixedSize(horizontal: false, vertical: true)
            footer(apply: nil)
        }
    }

    private func groupView(_ group: SeriesConflictGroup) -> some View {
        let prefix = model.info?.prefix ?? ""
        return VStack(alignment: .leading, spacing: 3) {
            Text(l.string(.seriesResolveKeeps, "\(prefix)-\(group.number)", group.keeperTitle))
                .font(.design(DesignTypography.baseSize, weight: DesignTypography.mediumWeight))
                .lineLimit(2)
            ForEach(group.moves) { move in
                Text(l.string(.seriesResolveMove, move.title, "\(prefix)-\(move.to)"))
                    .foregroundStyle(DesignColor.mut.color)
                    .lineLimit(2)
                    .padding(.leading, DesignSpacing.gap10)
            }
        }
    }

    private func footer(apply: Bool?) -> some View {
        HStack {
            Spacer()
            Button(l.string(.commonCancel)) { model.cancelResolve() }
                .keyboardShortcut(.cancelAction)
            if apply != nil {
                Button(l.string(.seriesResolveApply)) { model.applyResolve() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }
}
