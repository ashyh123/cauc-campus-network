import Foundation
import ServiceManagement

@MainActor
final class LaunchAtLoginManager: ObservableObject {
    @Published private(set) var isEnabled: Bool = false
    @Published private(set) var statusHint: String?

    init() {
        refresh()
    }

    func refresh(preservingHint: Bool = false) {
        switch SMAppService.mainApp.status {
        case .enabled:
            isEnabled = true
            if !preservingHint {
                statusHint = nil
            }
        case .requiresApproval:
            isEnabled = true
            statusHint = "请在系统设置 → 登录项中允许"
        case .notRegistered, .notFound:
            isEnabled = false
            if !preservingHint {
                statusHint = nil
            }
        @unknown default:
            isEnabled = false
            if !preservingHint {
                statusHint = nil
            }
        }
    }

    func setEnabled(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            statusHint = nil
            refresh()
        } catch {
            statusHint = enabled ? "开启自启失败，请从应用程序目录运行" : "无法取消开机自启"
            refresh(preservingHint: true)
        }
    }
}
