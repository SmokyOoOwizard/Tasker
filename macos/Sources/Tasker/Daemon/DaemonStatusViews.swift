import SwiftUI

struct DaemonStatusChip: View {
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l

    var body: some View {
        let appearance = DaemonStateAppearance(connection.state)
        HStack(spacing: DesignSpacing.gap8) {
            Circle()
                .fill(appearance.color.color)
                .frame(width: 8, height: 8)
            Text(l.string(.daemonTitle))
                .foregroundStyle(DesignColor.mut.color)
            Spacer(minLength: 0)
            Text(l.string(appearance.label))
                .foregroundStyle(appearance.color.color)
        }
        .font(.design(DesignTypography.captionSize))
        .padding(.horizontal, DesignSpacing.rowPadX)
        .padding(.vertical, DesignSpacing.rowPadY)
        .help(connection.state.status.map { "127.0.0.1:\($0.port)" } ?? "")
        .accessibilityElement(children: .combine)
    }
}

struct DaemonStateAppearance {
    var label: L10nKey
    var color: DesignColor

    init(_ state: DaemonConnectionState) {
        switch state {
        case .running:
            label = .daemonRunning
            color = .green
        case .starting:
            label = .daemonStarting
            color = .amber
        case .versionMismatch:
            label = .daemonStopped
            color = .amber
        case .stopped:
            label = .daemonStopped
            color = .red
        }
    }
}

struct DaemonGate<Content: View>: View {
    @Environment(DaemonConnection.self) private var connection
    @ViewBuilder var content: () -> Content

    var body: some View {
        switch connection.state {
        case .running:
            content()
        case .starting:
            DaemonProblemView(title: .daemonStarting, detail: nil, actions: [])
        case .versionMismatch(let daemon, let app, _):
            DaemonMismatchView(daemon: daemon, app: app)
        case .stopped(let failure):
            DaemonFailureView(failure: failure)
        }
    }
}

private struct DaemonMismatchView: View {
    let daemon: String?
    let app: String
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l

    var body: some View {
        DaemonProblemView(
            titleText: l.string(.daemonMismatchTitle),
            detail: l.string(.daemonMismatchBody, daemon ?? l.string(.daemonVersionUnknown), app),
            error: connection.updateError.map { l.string(.daemonUpdateFailed, $0) },
            actions: [DaemonAction(title: .daemonMismatchUpdate) { Task { await connection.updateDaemon() } }]
        )
    }
}

private struct DaemonFailureView: View {
    let failure: DaemonFailure
    @Environment(DaemonConnection.self) private var connection
    @Environment(\.localizer) private var l

    var body: some View {
        DaemonProblemView(
            titleText: l.string(.daemonStopped),
            detail: detail,
            actions: [DaemonAction(title: .daemonRetry) { connection.retry() }]
        )
    }

    private var detail: String {
        switch failure {
        case .helperMissing: l.string(.daemonMissing)
        case .notResponding: l.string(.daemonUnreachable, DaemonPaths().logsDirectory.path)
        case .startFailed(let message): l.string(.daemonStartFailed, message)
        case .updateFailed(let message): l.string(.daemonUpdateFailed, message)
        }
    }
}

struct DaemonAction {
    var title: L10nKey
    var perform: () -> Void
}

struct DaemonProblemView: View {
    @Environment(\.localizer) private var l
    let titleText: String
    let detail: String?
    var error: String?
    let actions: [DaemonAction]

    init(title: L10nKey, detail: String?, actions: [DaemonAction]) {
        self.titleText = ""
        self.titleKey = title
        self.detail = detail
        self.actions = actions
    }

    init(titleText: String, detail: String?, error: String? = nil, actions: [DaemonAction]) {
        self.titleText = titleText
        self.titleKey = nil
        self.detail = detail
        self.error = error
        self.actions = actions
    }

    private var titleKey: L10nKey?

    var body: some View {
        VStack(spacing: 10) {
            Text(titleKey.map { l.string($0) } ?? titleText)
                .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
            if let detail {
                Text(detail)
                    .font(.design(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.mut.color)
                    .multilineTextAlignment(.center)
                    .textSelection(.enabled)
            }
            if let error {
                Text(error)
                    .font(.design(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.red.color)
                    .multilineTextAlignment(.center)
                    .textSelection(.enabled)
            }
            ForEach(Array(actions.enumerated()), id: \.offset) { _, action in
                Button(l.string(action.title), action: action.perform)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(DesignSpacing.pagePadX)
        .frame(maxWidth: 480)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
