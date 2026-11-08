import Foundation
import Testing
import TaskerAPI
@testable import Tasker

actor LockLog {
    var acquired = 0
    var released = 0
    var acquireFailure: (any Error)?
    var probed: EditLockState = .none

    func setFailure(_ value: (any Error)?) { acquireFailure = value }
    func setProbe(_ value: EditLockState) { probed = value }

    func acquire() throws {
        if let acquireFailure { throw acquireFailure }
        acquired += 1
    }

    func release() { released += 1 }
    func probe() -> EditLockState { probed }
}

@MainActor
struct EditLockTests {
    func make(_ log: LockLog, heartbeat: Duration = .seconds(60)) -> EditLock {
        EditLock(
            ops: EditLockOps(
                acquire: { try await log.acquire() },
                release: { await log.release() },
                probe: { await log.probe() }),
            heartbeat: heartbeat)
    }

    @Test func acquireMarksLockAsMine() async {
        let log = LockLog()
        let lock = make(log)
        #expect(await lock.acquire() == .mine)
        #expect(lock.isMine)
        #expect(lock.holder == nil)
    }

    @Test func foreignLockShowsHolderAndRefusal() async {
        let log = LockLog()
        await log.setFailure(TaskerAPIError.locked(by: "Мария"))
        let lock = make(log)
        #expect(await lock.acquire() == .other("Мария"))
        #expect(lock.holder == "Мария")
        #expect(lock.refusal?.code == "entity.locked")
        #expect(lock.stop() == nil)
    }

    @Test func heartbeatRenewsWhileOpen() async throws {
        let log = LockLog()
        let lock = make(log, heartbeat: .milliseconds(20))
        await lock.acquire()
        lock.start()
        try await Task.sleep(for: .milliseconds(150))
        _ = lock.stop()
        #expect(await log.acquired >= 3)
    }

    @Test func stopReleasesOnlyMineAndOnlyOnce() async {
        let log = LockLog()
        let lock = make(log)
        await lock.acquire()
        let work = lock.stop()
        #expect(work != nil)
        await work?()
        #expect(await log.released == 1)
        #expect(lock.stop() == nil)
        #expect(await lock.acquire() == .mine)
        #expect(await log.acquired == 1)
    }

    @Test func relinquishReleasesAndAllowsReacquire() async {
        let log = LockLog()
        let lock = make(log)
        await lock.acquire()
        await lock.relinquish()
        #expect(lock.state == .none)
        #expect(await log.released == 1)
        #expect(await lock.acquire() == .mine)
    }

    @Test func refreshAdoptsForeignHolderAndTakesFreedLock() async {
        let log = LockLog()
        let lock = make(log)
        await lock.acquire()
        await log.setProbe(.other("Claude"))
        await lock.refresh()
        #expect(lock.holder == "Claude")
        await log.setProbe(.none)
        await lock.refresh()
        #expect(lock.isMine)
    }

    @Test func markLockedSwitchesToHolder() async {
        let log = LockLog()
        let lock = make(log)
        await lock.acquire()
        lock.markLocked(by: "Анна")
        #expect(lock.holder == "Анна")
        #expect(lock.stop() == nil)
    }

    @Test func failureClassification() {
        if case .modified = EditFailure(TaskerAPIError.modified()) {} else { Issue.record("modified") }
        if case .locked(let holder, _) = EditFailure(TaskerAPIError.locked(by: "Ира")) { #expect(holder == "Ира") } else { Issue.record("locked") }
        if case .other = EditFailure(CancellationError()) {} else { Issue.record("other") }
    }
}
