import SwiftUI
import TaskerAPI

struct ProjectGate<Content: View>: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    @ViewBuilder var content: () -> Content
    @State private var stats: ProjectQuery.Data.Project.Stats?

    var body: some View {
        Group {
            if let stats, OnboardingStep.isEmpty(stats) {
                OnboardingView(client: client, project: project, stats: stats)
            } else {
                content()
            }
        }
        .task(id: project.id) {
            stats = nil
            await LiveQuery.follow(client, ProjectQuery(id: project.id)) { data in
                stats = data.project?.stats
            }
        }
    }
}

struct OnboardingView: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    let stats: ProjectQuery.Data.Project.Stats
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var running: OnboardingStep?
    @State private var error: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text(String(project.name.prefix(1)).uppercased())
                    .font(.design(17, weight: DesignTypography.boldWeight))
                    .foregroundStyle(DesignColor.onAcc.color)
                    .frame(width: 36, height: 36)
                    .background(DesignColor.acc.color, in: RoundedRectangle(cornerRadius: 10))
                Text(l.string(.onboardingHeading, project.name))
                    .font(.design(DesignTypography.pageTitleSize, weight: DesignTypography.semiboldWeight))
                    .padding(.top, 16)
                    .padding(.bottom, 4)
                Text(l.string(.onboardingProgress, String(OnboardingStep.completed(stats))))
                    .foregroundStyle(DesignColor.mut.color)
                    .padding(.bottom, 22)
                VStack(spacing: DesignSpacing.gap8) {
                    ForEach(OnboardingStep.allCases) { step in
                        OnboardingRow(step: step, number: step.rawValue + 1, done: step.isDone(stats), enabled: step.isUnlocked(stats) && running == nil, busy: running == step) {
                            run(step)
                        }
                    }
                }
                if let error {
                    Text(l.string(.onboardingFailed, error))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.red.color)
                        .padding(.top, DesignSpacing.gap10)
                }
            }
            .frame(maxWidth: 560, alignment: .leading)
            .padding(.horizontal, DesignSpacing.pagePadX)
            .padding(.vertical, 56)
            .frame(maxWidth: .infinity)
        }
    }

    private func run(_ step: OnboardingStep) {
        running = step
        error = nil
        let onboarding = ProjectOnboarding(client: client, projectId: project.id, names: OnboardingNames(l))
        Task {
            do {
                try await onboarding.run(step)
                session.notice(l.string(step.doneKey))
                if let fresh = try? await client.graphQL.fetch(ProjectQuery(id: project.id)), let stats = fresh.project?.stats, !OnboardingStep.isEmpty(stats) {
                    session.notice(l.string(.onboardingReady))
                }
            } catch {
                self.error = error.localizedDescription
            }
            running = nil
        }
    }
}

private struct OnboardingRow: View {
    let step: OnboardingStep
    let number: Int
    let done: Bool
    let enabled: Bool
    let busy: Bool
    let action: () -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: DesignSpacing.gap10) {
            ZStack {
                Circle()
                    .fill(done ? DesignColor.green.color : .clear)
                    .overlay(Circle().strokeBorder(done ? DesignColor.green.color : DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
                if done {
                    MockupIconView(icon: .check, size: 12)
                        .foregroundStyle(DesignColor.onAcc.color)
                } else {
                    Text(verbatim: String(number))
                        .font(.design(DesignTypography.captionSize))
                        .foregroundStyle(DesignColor.dim.color)
                }
            }
            .frame(width: 22, height: 22)
            VStack(alignment: .leading, spacing: 1) {
                Text(l.string(step.titleKey))
                    .font(.designEmphasis(DesignTypography.baseSize))
                Text(l.string(step.hintKey))
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
            }
            Spacer(minLength: 0)
            if busy {
                ProgressView().controlSize(.small)
            } else if !done {
                Button(l.string(.onboardingCreate), action: action)
                    .disabled(!enabled)
            }
        }
        .padding(.horizontal, DesignSpacing.toastPadX)
        .padding(.vertical, DesignSpacing.gap10)
        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.panel))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.panel).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
        .opacity(done ? 0.7 : 1)
    }
}
