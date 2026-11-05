import SwiftUI
import TaskerAPI

struct LiveToastStack: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: DesignSpacing.gap8) {
            ForEach(live.toasts + session.toasts.toasts) { toast in
                HStack(spacing: DesignSpacing.gap8) {
                    MockupIconView(icon: toast.message.isFailure ? .warn : .check)
                        .foregroundStyle(toast.message.isFailure ? DesignColor.red.color : DesignColor.green.color)
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
                .onTapGesture {
                    live.dismiss(toast.id)
                    session.toasts.dismiss(toast.id)
                }
            }
        }
        .padding(.bottom, DesignSpacing.toastBottom)
        .animation(.easeOut(duration: 0.18), value: live.toasts + session.toasts.toasts)
    }
}

struct LiveStatusLabel: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l

    var body: some View {
        if live.address != nil, let text = LiveStatusText.text(connection: live.connection, l) {
            HStack(spacing: DesignSpacing.gap6) {
                Circle()
                    .fill(live.connection == .offline ? DesignColor.red.color : DesignColor.amber.color)
                    .frame(width: DesignLayout.statusDot, height: DesignLayout.statusDot)
                Text(text)
            }
            .font(.design(DesignTypography.captionSize))
            .foregroundStyle(DesignColor.dim.color)
            .help(live.address.map(\.name) ?? "")
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
        failed: @MainActor (any Error) -> Void = { _ in }
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
                failed(error)
            }
            try? await Task.sleep(for: retryDelay)
        }
    }
}
