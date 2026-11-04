import Foundation
import Observation

struct DaemonTimings: Sendable {
    var pollWhileRunning: Duration = .seconds(2)
    var pollWhileStarting: Duration = .milliseconds(300)
    var startTimeout: Duration = .seconds(30)
    var retryDelays: [Duration] = [.seconds(2), .seconds(5), .seconds(10)]
}

@MainActor
@Observable
final class DaemonConnection {
    private(set) var state: DaemonConnectionState = .starting
    private(set) var updateError: String?

    @ObservationIgnored private let files: DaemonFiles
    @ObservationIgnored private let transport: any DaemonTransport
    @ObservationIgnored private let service: any DaemonService
    @ObservationIgnored private let helperProgram: String?
    @ObservationIgnored private let appVersion: String
    @ObservationIgnored private let timings: DaemonTimings
    @ObservationIgnored private let sleep: @Sendable (Duration) async throws -> Void
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var failures = 0
    @ObservationIgnored private var retryRequested = false
    @ObservationIgnored private var updating = false

    init(
        files: DaemonFiles,
        transport: any DaemonTransport,
        service: any DaemonService,
        helperProgram: String?,
        appVersion: String,
        timings: DaemonTimings = DaemonTimings(),
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) {
        self.files = files
        self.transport = transport
        self.service = service
        self.helperProgram = helperProgram
        self.appVersion = appVersion
        self.timings = timings
        self.sleep = sleep
    }

    static func live() -> DaemonConnection {
        let paths = DaemonPaths()
        let helper = Bundle.main.bundleURL.appending(path: "Contents/Helpers/tasker-mcpd")
        var environment: [String: String] = [:]
        if let home = ProcessInfo.processInfo.environment["TASKER_HOME"], !home.isEmpty {
            environment["TASKER_HOME"] = paths.dataDirectory.path
        }
        return DaemonConnection(
            files: DaemonFiles(paths: paths),
            transport: URLSessionDaemonTransport(),
            service: DaemonLaunchAgent(paths: paths, runner: SystemCommandRunner(), environment: environment),
            helperProgram: FileManager.default.isExecutableFile(atPath: helper.path) ? helper.path : nil,
            appVersion: AppInfo.version
        )
    }

    func start() {
        guard task == nil else { return }
        task = Task { [weak self] in
            await self?.run()
        }
    }

    func stop() {
        task?.cancel()
        task = nil
    }

    func retry() {
        failures = 0
        retryRequested = true
    }

    func updateDaemon() async {
        guard case .versionMismatch(_, _, let status) = state, let helperProgram else { return }
        updateError = nil
        state = .starting
        updating = true
        defer { updating = false }
        do {
            if status.isSupervised, let info = files.readInfo() {
                let result = try await transport.upgrade(info: info, program: helperProgram, timeoutSeconds: 60)
                if !result.ok { throw DaemonServiceError(message: result.message) }
            } else if let info = files.readInfo() {
                try await transport.stop(info: info)
                await waitUntilStopped()
            }
        } catch {
            updateError = Self.describe(error)
        }
        retry()
    }

    func run() async {
        while !Task.isCancelled {
            let delay = await step()
            await pause(delay)
        }
    }

    func step() async -> Duration {
        if updating { return timings.pollWhileStarting }
        if let status = await currentStatus() {
            failures = 0
            apply(status)
            return timings.pollWhileRunning
        }

        guard let helperProgram else {
            state = .stopped(.helperMissing)
            return timings.retryDelays.last ?? .seconds(10)
        }

        state = .starting
        if !files.isRunning() {
            do {
                try await service.start(program: helperProgram)
            } catch {
                return fail(.startFailed(Self.describe(error)))
            }
        }

        if let status = await waitForStatus() {
            failures = 0
            apply(status)
            return timings.pollWhileRunning
        }
        return fail(.notResponding)
    }

    private func fail(_ failure: DaemonFailure) -> Duration {
        state = .stopped(failure)
        let index = min(failures, timings.retryDelays.count - 1)
        failures += 1
        return timings.retryDelays[index]
    }

    private func apply(_ status: DaemonStatus) {
        guard let version = status.activeVersion else {
            state = status.isSupervised ? .starting : .versionMismatch(daemon: nil, app: appVersion, status: status)
            return
        }
        if version != appVersion {
            state = .versionMismatch(daemon: version, app: appVersion, status: status)
        } else if status.isSettled {
            updateError = nil
            state = .running(status)
        } else {
            state = .starting
        }
    }

    private func currentStatus() async -> DaemonStatus? {
        guard files.isRunning(), let info = files.readInfo() else { return nil }
        return try? await transport.status(of: info)
    }

    private func waitForStatus() async -> DaemonStatus? {
        let clock = ContinuousClock()
        let deadline = clock.now + timings.startTimeout
        while clock.now < deadline, !Task.isCancelled {
            if let status = await currentStatus() { return status }
            try? await sleep(timings.pollWhileStarting)
        }
        return nil
    }

    private func waitUntilStopped() async {
        let clock = ContinuousClock()
        let deadline = clock.now + .seconds(15)
        while files.isRunning(), clock.now < deadline {
            try? await sleep(timings.pollWhileStarting)
        }
    }

    private func pause(_ duration: Duration) async {
        let slice = Duration.milliseconds(100)
        var waited = Duration.zero
        while waited < duration, !retryRequested, !Task.isCancelled {
            try? await sleep(slice)
            waited += slice
        }
        retryRequested = false
    }

    private static func describe(_ error: any Error) -> String {
        if let error = error as? DaemonServiceError { return error.message }
        if let error = error as? DaemonTransportError { return "HTTP \(error.statusCode)" }
        return error.localizedDescription
    }
}
