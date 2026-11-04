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

struct LiveMessageView: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.design(DesignTypography.baseSize))
            .foregroundStyle(DesignColor.mut.color)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
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
