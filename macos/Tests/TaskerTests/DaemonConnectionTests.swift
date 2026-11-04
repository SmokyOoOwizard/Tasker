import Foundation
import Testing
@testable import Tasker

private final class FakeDaemon: DaemonTransport, DaemonService, @unchecked Sendable {
    var version = "0.1.0"
    var settled = true
    var supervised = true
    var reachable = false
    var startCalls = 0
    var startError: String?
    var upgradeCalls = 0
    var stopCalls = 0
    var upgradeOk = true
    let onStart: @Sendable () -> Void

    init(onStart: @escaping @Sendable () -> Void = {}) {
        self.onStart = onStart
    }

    var status: DaemonStatus {
        DaemonStatus(
            pid: 1,
            port: 5719,
            workers: supervised ? [DaemonWorker(pid: 2, version: version, build: "b", role: "active")] : [],
            workspaces: [DaemonWorkspace(path: "/x", state: settled ? "open" : "opening", error: nil)]
        )
    }

    func status(of info: DaemonInfo) async throws -> DaemonStatus {
        guard reachable else { throw URLError(.cannotConnectToHost) }
        return status
    }

    func upgrade(info: DaemonInfo, program: String, timeoutSeconds: Int) async throws -> DaemonUpgradeResult {
        upgradeCalls += 1
        if upgradeOk { version = "0.1.0" }
        return DaemonUpgradeResult(ok: upgradeOk, message: upgradeOk ? "ok" : "refused")
    }

    func stop(info: DaemonInfo) async throws {
        stopCalls += 1
        reachable = false
    }

    func start(program: String) async throws {
        startCalls += 1
        if let startError { throw DaemonServiceError(message: startError) }
        reachable = true
        onStart()
    }
}

private func makeDirectory() throws -> DaemonPaths {
    let root = FileManager.default.temporaryDirectory.appending(path: "tasker-daemon-\(UUID().uuidString)")
    let paths = DaemonPaths(dataDirectory: root, launchAgentsDirectory: root.appending(path: "agents"))
    try FileManager.default.createDirectory(at: paths.daemonDirectory, withIntermediateDirectories: true)
    return paths
}

private func writeInfo(_ paths: DaemonPaths) throws {
    try JSONEncoder().encode(DaemonInfo(pid: 1, port: 5719, token: "t")).write(to: paths.infoFile)
}

private func hold(_ paths: DaemonPaths) -> Int32 {
    let descriptor = open(paths.lockFile.path, O_RDWR | O_CREAT, 0o600)
    flock(descriptor, LOCK_EX | LOCK_NB)
    return descriptor
}

private final class LockBox: @unchecked Sendable {
    var descriptor: Int32 = -1

    func acquire(_ paths: DaemonPaths) {
        release()
        descriptor = hold(paths)
    }

    func release() {
        if descriptor >= 0 { close(descriptor) }
        descriptor = -1
    }
}

@MainActor
struct DaemonConnectionTests {
    private func connection(
        _ fake: FakeDaemon,
        paths: DaemonPaths,
        helper: String? = "/app/tasker-mcpd",
        version: String = "0.1.0"
    ) -> DaemonConnection {
        DaemonConnection(
            files: DaemonFiles(paths: paths),
            transport: fake,
            service: fake,
            helperProgram: helper,
            appVersion: version,
            timings: DaemonTimings(startTimeout: .seconds(1)),
            sleep: { _ in try await Task.sleep(for: .milliseconds(1)) }
        )
    }

    @Test func startsDaemonWhenNotRunning() async throws {
        let paths = try makeDirectory()
        let lock = LockBox()
        let fake = FakeDaemon {
            try? writeInfo(paths)
            lock.acquire(paths)
        }
        let model = connection(fake, paths: paths)
        _ = await model.step()
        #expect(fake.startCalls == 1)
        guard case .running(let status) = model.state else {
            Issue.record("состояние \(model.state)")
            return
        }
        #expect(status.activeVersion == "0.1.0")
        lock.release()
    }

    @Test func connectsToRunningDaemonWithoutStartingIt() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        defer { close(descriptor) }
        let fake = FakeDaemon()
        fake.reachable = true
        let model = connection(fake, paths: paths)
        _ = await model.step()
        #expect(fake.startCalls == 0)
        #expect(model.state.status != nil)
    }

    @Test func blocksOnVersionMismatch() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        defer { close(descriptor) }
        let fake = FakeDaemon()
        fake.reachable = true
        fake.version = "0.0.9"
        let model = connection(fake, paths: paths)
        _ = await model.step()
        guard case .versionMismatch(let daemon, let app, _) = model.state else {
            Issue.record("состояние \(model.state)")
            return
        }
        #expect(daemon == "0.0.9")
        #expect(app == "0.1.0")
    }

    @Test func updateReplacesWorkerAndUnblocks() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        defer { close(descriptor) }
        let fake = FakeDaemon()
        fake.reachable = true
        fake.version = "0.0.9"
        let model = connection(fake, paths: paths)
        _ = await model.step()
        await model.updateDaemon()
        _ = await model.step()
        #expect(fake.upgradeCalls == 1)
        guard case .running = model.state else {
            Issue.record("состояние \(model.state)")
            return
        }
    }

    @Test func failedUpdateKeepsMismatchAndShowsError() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        defer { close(descriptor) }
        let fake = FakeDaemon()
        fake.reachable = true
        fake.version = "0.0.9"
        fake.upgradeOk = false
        let model = connection(fake, paths: paths)
        _ = await model.step()
        await model.updateDaemon()
        _ = await model.step()
        #expect(model.updateError == "refused")
        guard case .versionMismatch = model.state else {
            Issue.record("состояние \(model.state)")
            return
        }
    }

    @Test func unsupervisedDaemonIsStoppedForUpdate() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        let fake = FakeDaemon()
        fake.reachable = true
        fake.supervised = false
        let model = connection(fake, paths: paths)
        _ = await model.step()
        guard case .versionMismatch(let daemon, _, _) = model.state else {
            Issue.record("состояние \(model.state)")
            return
        }
        #expect(daemon == nil)
        close(descriptor)
        await model.updateDaemon()
        #expect(fake.stopCalls == 1)
    }

    @Test func missingHelperStopsWithMessage() async throws {
        let paths = try makeDirectory()
        let fake = FakeDaemon()
        let model = connection(fake, paths: paths, helper: nil)
        _ = await model.step()
        #expect(model.state == .stopped(.helperMissing))
        #expect(fake.startCalls == 0)
    }

    @Test func startFailureIsReportedAndRetriedWithBackoff() async throws {
        let paths = try makeDirectory()
        let fake = FakeDaemon()
        fake.startError = "bootstrap failed"
        let model = connection(fake, paths: paths)
        let first = await model.step()
        let second = await model.step()
        let third = await model.step()
        let fourth = await model.step()
        #expect(model.state == .stopped(.startFailed("bootstrap failed")))
        #expect(first == .seconds(2))
        #expect(second == .seconds(5))
        #expect(third == .seconds(10))
        #expect(fourth == .seconds(10))
        #expect(fake.startCalls == 4)
    }

    @Test func unresponsiveDaemonReportsNotResponding() async throws {
        let paths = try makeDirectory()
        let fake = FakeDaemon { }
        let model = connection(fake, paths: paths)
        fake.reachable = false
        let descriptor = hold(paths)
        defer { close(descriptor) }
        try writeInfo(paths)
        _ = await model.step()
        #expect(model.state == .stopped(.notResponding))
        #expect(fake.startCalls == 0)
    }

    @Test func recoversAfterDaemonRestart() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let lock = LockBox()
        lock.acquire(paths)
        let fake = FakeDaemon {
            try? writeInfo(paths)
            lock.acquire(paths)
        }
        fake.reachable = true
        let model = connection(fake, paths: paths)
        _ = await model.step()
        #expect(model.state.status != nil)

        lock.release()
        fake.reachable = false
        _ = await model.step()
        #expect(fake.startCalls == 1)
        #expect(model.state.status != nil)
        lock.release()
    }

    @Test func unsettledWorkspacesKeepStartingState() async throws {
        let paths = try makeDirectory()
        try writeInfo(paths)
        let descriptor = hold(paths)
        defer { close(descriptor) }
        let fake = FakeDaemon()
        fake.reachable = true
        fake.settled = false
        let model = connection(fake, paths: paths)
        _ = await model.step()
        #expect(model.state == .starting)
    }
}

struct DaemonPlatformTests {
    @Test func pathsFollowTaskerHome() {
        let paths = DaemonPaths(environment: ["TASKER_HOME": "/tmp/th", "TASKER_DAEMON_LABEL": "x.y"], home: URL(fileURLWithPath: "/Users/u"))
        #expect(paths.infoFile.path == "/tmp/th/mcp/daemon.json")
        #expect(paths.lockFile.path == "/tmp/th/mcp/daemon.lock")
        #expect(paths.plistFile.path == "/Users/u/Library/LaunchAgents/x.y.plist")
    }

    @Test func defaultPathsMatchTheConsole() {
        let paths = DaemonPaths(environment: [:], home: URL(fileURLWithPath: "/Users/u"))
        #expect(paths.infoFile.path == "/Users/u/Library/Application Support/Tasker/mcp/daemon.json")
        #expect(paths.label == DaemonPaths.defaultLabel)
    }

    @Test func decodesDaemonStatusFromTheDaemon() throws {
        let json = """
        {"pid":10,"port":5719,"settingsPort":5719,"startedAt":"2026-11-03T22:26:50+00:00",
         "workspaces":[{"kind":"files","path":"/p","state":"open","key":"p","error":null}],
         "workers":[{"pid":11,"version":"0.1.0","build":"ab12","role":"active","startedAt":"2026-11-03T22:26:50+00:00"},
                    {"pid":12,"version":"0.0.9","build":"cd34","role":"draining","startedAt":"2026-11-03T22:00:00+00:00"}]}
        """
        let status = try JSONDecoder().decode(DaemonStatus.self, from: Data(json.utf8))
        #expect(status.activeVersion == "0.1.0")
        #expect(status.isSettled)
        #expect(status.isSupervised)
    }

    @Test func plistRunsTheBundledDaemonAndKeepsItAlive() {
        let paths = DaemonPaths(dataDirectory: URL(fileURLWithPath: "/d"), launchAgentsDirectory: URL(fileURLWithPath: "/a"))
        let agent = DaemonLaunchAgent(paths: paths, runner: SystemCommandRunner(), environment: ["TASKER_HOME": "/d&x"])
        let plist = agent.plist(program: "/Applications/Tasker.app/Contents/Helpers/tasker-mcpd")
        #expect(plist.contains("<string>/Applications/Tasker.app/Contents/Helpers/tasker-mcpd</string>"))
        #expect(plist.contains("<string>--detached</string>"))
        #expect(plist.contains("<key>KeepAlive</key><true/>"))
        #expect(plist.contains("<key>TASKER_HOME</key><string>/d&amp;x</string>"))
        #expect(plist.contains("<key>Label</key><string>\(DaemonPaths.defaultLabel)</string>"))
    }

    @Test func lockProbeSeesHeldLock() throws {
        let paths = try makeDirectory()
        let files = DaemonFiles(paths: paths)
        #expect(!files.isRunning())
        let descriptor = hold(paths)
        #expect(files.isRunning())
        close(descriptor)
        #expect(!files.isRunning())
    }
}
