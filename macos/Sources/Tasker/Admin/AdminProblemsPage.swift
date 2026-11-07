import SwiftUI
import TaskerAPI

private struct ResolveTarget: Identifiable {
    var project: UUID
    var series: UUID
    var id: UUID { series }
}

struct AdminProblemsPage: View {
    let model: AdminModel
    let client: TaskerAPIClient
    @Environment(\.localizer) private var l
    @State private var resolving: ResolveTarget?
    @State private var failure: String?
    @State private var confirmingCleanup: (project: UUID, name: String)?

    var body: some View {
        SettingsPageFrame(title: .adminPageProblems, lead: l.string(.adminProblemsLead)) {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                if let failure { Text(failure).foregroundStyle(DesignColor.red.color) }
                if model.problems.items.isEmpty {
                    SettingsCard {
                        HStack(spacing: 6) {
                            MockupIconView(icon: .check, size: 13)
                            Text(l.string(.adminProblemsClean))
                        }
                        .foregroundStyle(DesignColor.green.color)
                    }
                } else {
                    actions
                    VStack(spacing: 0) {
                        ForEach(model.problems.items) { problem in
                            ProblemRow(problem: problem)
                        }
                    }
                }
            }
        }
        .sheet(item: $resolving) { target in
            SeriesResolveHost(client: client, project: target.project, series: target.series) {
                resolving = nil
                model.load()
            }
        }
        .confirmationDialog(
            l.string(.adminCleanupTitle), isPresented: Binding(get: { confirmingCleanup != nil }, set: { if !$0 { confirmingCleanup = nil } }),
            presenting: confirmingCleanup
        ) { target in
            Button(l.string(.adminCleanupAction), role: .destructive) {
                Task { failure = await model.cleanInvalidLinks(project: target.project) }
            }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { target in
            Text(l.string(.adminCleanupBody, target.name))
        }
    }

    @ViewBuilder
    private var actions: some View {
        let conflicts = model.problems.projectsWithConflicts
        let links = model.problems.projectsWithInvalidLinks
        if !conflicts.isEmpty || !links.isEmpty {
            HStack(spacing: DesignSpacing.gap8) {
                ForEach(conflicts, id: \.series) { conflict in
                    Button(l.string(.adminResolveConflicts, conflict.prefix)) {
                        resolving = ResolveTarget(project: conflict.project, series: conflict.series)
                    }
                    .disabled(model.busy)
                }
                ForEach(links, id: \.project) { link in
                    Button(l.string(.adminCleanupButton, link.name)) { confirmingCleanup = (link.project, link.name) }
                        .disabled(model.busy)
                }
            }
        }
    }
}

private struct ProblemRow: View {
    let problem: AdminProblem
    @Environment(\.localizer) private var l

    var body: some View {
        SettingsRow {
            HStack(spacing: 6) {
                MockupIconView(icon: .warn, size: 13).foregroundStyle(DesignColor.amber.color)
                Text(title)
            }
        } meta: {
            if let detail { Text(detail).textSelection(.enabled).fixedSize(horizontal: false, vertical: true) }
        } actions: {
            EmptyView()
        }
    }

    private var title: String {
        switch problem.kind {
        case .unreadableFile: l.string(.adminProblemUnreadable)
        case .secondFolder: l.string(.adminProblemSecondFolder)
        case .numberConflict(let value): l.string(.adminProblemNumber, "\(value.prefix)-\(value.number)", value.projectName)
        case .prefixConflict(let project, let prefix): l.string(.adminProblemPrefix, prefix, project)
        case .invalidSeries(let project, let count): l.string(.adminProblemInvalidSeries, count, project)
        case .unreadableSeries(let project, let count): l.string(.adminProblemUnreadableSeries, String(count), project)
        case .invalidLinks(_, let name, let count): l.string(.adminProblemInvalidLinks, count, name)
        case .unreadableLinks(let project, let count): l.string(.adminProblemUnreadableLinks, String(count), project)
        case .linkCycle(let value): l.string(.adminProblemCycle, value.typeName, value.projectName)
        }
    }

    private var detail: String? {
        switch problem.kind {
        case .unreadableFile(let path, let error): "\(path): \(error)"
        case .secondFolder(let path): l.string(.adminProblemSecondFolderHint, path)
        case .numberConflict(let value): l.string(.countTasks, value.taskCount)
        case .linkCycle(let value): value.path
        default: nil
        }
    }
}
