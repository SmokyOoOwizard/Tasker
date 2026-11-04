import SwiftUI
import TaskerAPI

struct LiveToastStack: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: DesignSpacing.gap8) {
            ForEach(live.toasts) { toast in
                HStack(spacing: DesignSpacing.gap8) {
                    MockupIconView(icon: .check)
                        .foregroundStyle(DesignColor.green.color)
                    Text(toast.message.text(l))
                        .lineLimit(2)
                }
                .font(.design(DesignTypography.baseSize))
                .padding(.horizontal, DesignSpacing.toastPadX)
                .padding(.vertical, DesignSpacing.toastPadY)
                .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.panel))
                .overlay(RoundedRectangle(cornerRadius: DesignRadius.panel).stroke(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
                .designShadow(DesignShadow.popup, scheme: scheme)
                .frame(maxWidth: DesignLayout.toastMaxWidth)
                .transition(.move(edge: .bottom).combined(with: .opacity))
                .onTapGesture { live.dismiss(toast.id) }
            }
        }
        .padding(.bottom, DesignSpacing.toastBottom)
        .animation(.easeOut(duration: 0.18), value: live.toasts)
    }
}

struct LiveStatusLabel: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack(spacing: DesignSpacing.gap6) {
                Circle()
                    .fill(color.color)
                    .frame(width: DesignLayout.statusDot, height: DesignLayout.statusDot)
                Text(LiveStatusText.text(connection: live.connection, lastUpdate: live.lastUpdate, now: context.date, l))
                    .monospacedDigit()
            }
            .font(.design(DesignTypography.captionSize))
            .foregroundStyle(DesignColor.dim.color)
        }
        .help(live.address.map(\.name) ?? "")
    }

    private var color: DesignColor {
        switch live.connection {
        case .live: .green
        case .connecting: .amber
        case .offline: .red
        }
    }
}

struct ProjectScope<Content: View>: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @Environment(\.localizer) private var l
    @State private var creating = false
    @ViewBuilder var content: () -> Content

    var body: some View {
        if let client = live.client {
            if let project = selection.current {
                ProjectGate(client: client, project: project, content: content)
                    .id(project.id)
            } else if live.projects != nil {
                VStack(spacing: DesignSpacing.gap10) {
                    Text(l.string(.overviewNoProjects))
                        .foregroundStyle(DesignColor.mut.color)
                    Button(l.string(.projectNew)) { creating = true }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .sheet(isPresented: $creating) { NewProjectSheet() }
            } else {
                LiveMessageView(text: l.string(.liveConnecting))
            }
        } else {
            LiveMessageView(text: l.string(.overviewNoWorkspace))
        }
    }
}

struct LiveOverviewView: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection

    var body: some View {
        ProjectScope {
            if let client = live.client, let project = selection.current {
                ScrollView {
                    LiveProjectCard(client: client, project: project)
                        .padding(DesignSpacing.pagePadX)
                }
            }
        }
    }
}

struct LiveMessageView: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.design(DesignTypography.baseSize))
            .foregroundStyle(DesignColor.mut.color)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct LiveProjectCard: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    @Environment(\.localizer) private var l
    @State private var tasks: ProjectTasksQuery.Data.Project.Tasks?
    @State private var statuses: [UUID: String] = [:]
    @State private var prefixes: [UUID: String] = [:]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignSpacing.gap8) {
                Text(project.name)
                    .font(.designEmphasis(DesignTypography.baseSize))
                Spacer(minLength: 0)
                if let tasks {
                    Text(l.taskCount(tasks.totalCount))
                        .foregroundStyle(DesignColor.dim.color)
                        .monospacedDigit()
                }
            }
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignSpacing.rowPadY)
            DesignDivider()
            if let tasks, tasks.data.isEmpty {
                Text(l.string(.overviewNoTasks))
                    .foregroundStyle(DesignColor.mut.color)
                    .padding(.horizontal, DesignSpacing.rowPadX)
                    .frame(height: DesignLayout.taskRowHeight)
            }
            ForEach(tasks?.data ?? [], id: \.id) { task in
                HStack(spacing: DesignSpacing.gap10) {
                    Text(reference(task) ?? "")
                        .font(.design(DesignTypography.monoSize, mono: true))
                        .foregroundStyle(DesignColor.dim.color)
                    Text(task.title)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                    Text(statuses[task.statusId] ?? "")
                        .foregroundStyle(DesignColor.mut.color)
                }
                .padding(.horizontal, DesignSpacing.rowPadX)
                .frame(height: DesignLayout.taskRowHeight)
            }
        }
        .font(.design(DesignTypography.baseSize))
        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.panel))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.panel).stroke(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
        .task {
            await LiveQuery.follow(client, ProjectTasksQuery(projectId: project.id, sort: "-updated", limit: 10)) { data in
                tasks = data.project?.tasks
            }
        }
        .task {
            await LiveQuery.follow(client, ProjectVocabularyQuery(projectId: project.id)) { data in
                statuses = Dictionary(data.project?.statuses.data.map { ($0.id, $0.name) } ?? [], uniquingKeysWith: { first, _ in first })
                prefixes = Dictionary(data.project?.seriesList.data.map { ($0.id, $0.prefix) } ?? [], uniquingKeysWith: { first, _ in first })
            }
        }
    }

    private func reference(_ task: TaskListFields) -> String? {
        task.seriesNumbers.lazy.compactMap { number in prefixes[number.seriesId].map { "\($0)-\(number.number)" } }.first
    }
}

enum LiveQuery {
    @MainActor
    static func follow<Query: GraphQLQuery>(
        _ client: TaskerAPIClient,
        _ query: Query,
        retryDelay: Duration = .seconds(2),
        value: @MainActor (Query.Data) -> Void,
        failed: @MainActor (String) -> Void = { _ in }
    ) async {
        while !Task.isCancelled {
            do {
                for try await data in client.graphQL.watch(query) {
                    value(data)
                }
                return
            } catch is CancellationError {
                return
            } catch {
                failed(error.localizedDescription)
            }
            try? await Task.sleep(for: retryDelay)
        }
    }
}
