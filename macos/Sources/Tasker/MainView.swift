import SwiftUI

struct MainView: View {
    var body: some View {
        VStack(spacing: 6) {
            Text("Tasker")
                .font(.system(size: 34, weight: .semibold))
            Text("Версия \(AppInfo.version)")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .frame(minWidth: 640, minHeight: 420)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}
