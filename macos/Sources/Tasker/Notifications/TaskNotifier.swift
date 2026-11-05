import AppKit
import Foundation
import Observation
import SwiftUI
import TaskerAPI
import UserNotifications

@MainActor
protocol NotificationCenterClient: AnyObject {
    func authorizationStatus() async -> UNAuthorizationStatus
    func requestAuthorization() async -> Bool
    func add(_ request: UNNotificationRequest) async throws
}

@MainActor
final class SystemNotificationCenter: NotificationCenterClient {
    private let center: UNUserNotificationCenter

    init(center: UNUserNotificationCenter = .current()) {
        self.center = center
    }

    func authorizationStatus() async -> UNAuthorizationStatus {
        await center.notificationSettings().authorizationStatus
    }

    func requestAuthorization() async -> Bool {
        (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
    }

    func add(_ request: UNNotificationRequest) async throws {
        try await center.add(request)
    }
}

@MainActor
final class TaskNotifier: NSObject, UNUserNotificationCenterDelegate {
    nonisolated static let urlKey = "url"

    private let client: any NotificationCenterClient
    private let defaults: UserDefaults
    private let isForeground: () -> Bool
    private let ownAuthorKey: String
    var onOpen: (TaskerLink) -> Void = { _ in }

    init(
        client: any NotificationCenterClient, defaults: UserDefaults = .standard,
        isForeground: @escaping () -> Bool = { NSApp.isActive }, ownAuthorKey: String = ChangeAuthor.localKey
    ) {
        self.client = client
        self.defaults = defaults
        self.isForeground = isForeground
        self.ownAuthorKey = ownAuthorKey
    }

    static func live(defaults: UserDefaults = .standard) -> TaskNotifier {
        let notifier = TaskNotifier(client: SystemNotificationCenter(), defaults: defaults)
        UNUserNotificationCenter.current().delegate = notifier
        return notifier
    }

    private var localizer: Localizer {
        Localizer(choice: AppLanguage(storedValue: defaults.string(forKey: AppLanguage.storageKey) ?? ""))
    }

    func prepare() async {
        guard NotificationPreferences(defaults: defaults).enabled else { return }
        await requestIfNeeded()
    }

    func status() async -> UNAuthorizationStatus {
        await client.authorizationStatus()
    }

    func requestIfNeeded() async {
        if await client.authorizationStatus() == .notDetermined {
            _ = await client.requestAuthorization()
        }
    }

    func handle(_ update: LiveUpdate, path: String) async {
        let preferences = NotificationPreferences(defaults: defaults)
        guard preferences.enabled, !isForeground() else { return }
        guard let notice = ChangeNotice(update, ownAuthorKey: ownAuthorKey), preferences.allows(notice.kind) else { return }
        let status = await client.authorizationStatus()
        guard status == .authorized || status == .provisional else { return }
        let link = TaskerLink(path: path, target: notice.target())
        let content = UNMutableNotificationContent()
        content.title = WorkspacePath.name(path)
        content.body = notice.message.text(localizer)
        content.sound = .default
        content.threadIdentifier = WorkspaceLinkID.make(path: path)
        content.userInfo = [Self.urlKey: link.string]
        let request = UNNotificationRequest(identifier: "task-" + notice.taskID.uuidString, content: content, trigger: nil)
        try? await client.add(request)
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard response.actionIdentifier == UNNotificationDefaultActionIdentifier else { return }
        let info = response.notification.request.content.userInfo
        let text = info[Self.urlKey] as? String
        await MainActor.run {
            guard let text, let link = TaskerLink.parse(text) else { return }
            onOpen(link)
        }
    }
}

extension EnvironmentValues {
    @Entry var taskNotifier: TaskNotifier?
}
