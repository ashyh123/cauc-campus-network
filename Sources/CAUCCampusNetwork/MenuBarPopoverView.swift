import AppKit
import CAUCCampusCore
import SwiftUI

struct MenuBarPopoverView: View {
    @ObservedObject var controller: AppController
    @ObservedObject var launchAtLogin: LaunchAtLoginManager
    var onDismissPanel: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            headerSection
            dualStackSection

            if let quota = controller.displayedQuota {
                QuotaCardView(
                    quota: quota,
                    cachedTimeText: controller.isShowingCachedQuota ? controller.cachedQuotaTimeText : nil
                )
                .transition(.opacity.combined(with: .scale(scale: 0.98)))
            } else if controller.showQuotaUnreachableHint {
                QuotaUnavailableCard()
                    .transition(.opacity.combined(with: .scale(scale: 0.98)))
            }

            primaryActionButton
            footerSection
        }
        .padding(14)
        .frame(width: 312)
        .animation(.easeInOut(duration: 0.22), value: controller.connectivity)
        .animation(.easeInOut(duration: 0.22), value: controller.displayedQuota)
        .animation(.easeInOut(duration: 0.22), value: controller.showQuotaUnreachableHint)
        .animation(.easeInOut(duration: 0.18), value: controller.isBusy)
    }

    // MARK: - Header

    private var headerSection: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Circle()
                    .fill(overallStatusColor)
                    .frame(width: 9.5, height: 9.5)
                    .shadow(color: overallStatusColor.opacity(0.45), radius: 4)

                Text("CAUC 校园网")
                    .font(.system(size: 14, weight: .semibold, design: .rounded))

                if controller.isBusy {
                    ProgressView()
                        .controlSize(.mini)
                }

                Spacer(minLength: 8)

                HeaderIconButton(
                    systemName: controller.isBusy ? "xmark" : "arrow.clockwise",
                    helpText: controller.isBusy ? "停止检测" : "重新检测"
                ) {
                    if controller.isBusy {
                        controller.stopChecking()
                    } else {
                        controller.refresh(force: true)
                    }
                }
            }

            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(controller.message)
                    .font(.system(size: 11.5))
                    .foregroundColor(.secondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)

                Spacer(minLength: 4)

                if let checkedTime = controller.lastCheckedTime {
                    Text(checkedTime)
                        .font(.system(size: 10.5))
                        .monospacedDigit()
                        .foregroundColor(.secondary.opacity(0.75))
                }
            }
        }
        .padding(.horizontal, 4)
    }

    // MARK: - Dual Stack Cards

    private var dualStackSection: some View {
        HStack(spacing: 10) {
            StackStatusCard(
                title: "IPv4",
                state: stackState(for: controller.connectivity?.ipv4)
            )
            StackStatusCard(
                title: "IPv6",
                state: stackState(for: controller.connectivity?.ipv6)
            )
        }
    }

    private func stackState(for reachable: Bool?) -> StackStatusCard.State {
        if let reachable {
            return reachable ? .online : .offline
        }
        return controller.isBusy ? .checking : .unknown
    }

    // MARK: - Primary Button

    private var primaryActionButton: some View {
        Button {
            if controller.isBusy {
                controller.stopChecking()
            } else if controller.connectivity?.isDualStackOnline == true {
                controller.refresh(force: true)
            } else {
                controller.startAuthentication()
                onDismissPanel()
            }
        } label: {
            primaryButtonLabel
        }
        .buttonStyle(PrimaryActionButtonStyle(style: primaryButtonStyle))
    }

    @ViewBuilder
    private var primaryButtonLabel: some View {
        if controller.isBusy {
            HStack(spacing: 7) {
                ProgressView()
                    .controlSize(.small)
                Text(controller.isAuthenticating ? "正在等待认证…" : "正在检测网络…")
                    .font(.system(size: 12.5, weight: .medium))
                Spacer(minLength: 4)
                Text("停止")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.secondary)
            }
            .padding(.horizontal, 14)
        } else if controller.connectivity?.isDualStackOnline == true {
            HStack(spacing: 6) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 12.5, weight: .semibold))
                Text("双栈已连通 · 重新检测")
                    .font(.system(size: 12.5, weight: .semibold))
            }
        } else {
            HStack(spacing: 6) {
                Image(systemName: "bolt.fill")
                    .font(.system(size: 12, weight: .semibold))
                Text("开始认证")
                    .font(.system(size: 12.5, weight: .semibold))
            }
        }
    }

    private var primaryButtonStyle: PrimaryActionButtonStyle.Kind {
        if controller.isBusy {
            return .busy
        }
        if controller.connectivity?.isDualStackOnline == true {
            return .online
        }
        return .accent
    }

    // MARK: - Footer

    private var footerSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 7) {
                    Image(systemName: "power.circle")
                        .font(.system(size: 12.5))
                        .foregroundColor(.secondary)
                    Text("登录时自动启动")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.primary.opacity(0.88))
                    Spacer()
                    Toggle(
                        "",
                        isOn: Binding(
                            get: { launchAtLogin.isEnabled },
                            set: { launchAtLogin.setEnabled($0) }
                        )
                    )
                    .toggleStyle(.switch)
                    .controlSize(.mini)
                    .labelsHidden()
                }

                if let hint = launchAtLogin.statusHint {
                    Text(hint)
                        .font(.system(size: 10.5))
                        .foregroundColor(.orange)
                        .padding(.leading, 20)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(Color.primary.opacity(0.045))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(
                        LinearGradient(
                            colors: [Color.white.opacity(0.22), Color.primary.opacity(0.08)],
                            startPoint: .top,
                            endPoint: .bottom
                        ),
                        lineWidth: 0.5
                    )
            )

            HStack(spacing: 6) {
                FooterActionButton(title: "认证页", systemImage: "safari") {
                    controller.openPortalManually()
                    onDismissPanel()
                }
                FooterActionButton(title: "Wi-Fi 设置", systemImage: "gearshape") {
                    controller.openWiFiSettings()
                    onDismissPanel()
                }
                Spacer()
                FooterActionButton(title: "退出", systemImage: nil) {
                    controller.quit()
                }
            }
            .padding(.horizontal, 2)
        }
    }

    private var overallStatusColor: Color {
        if let status = controller.connectivity {
            if status.isDualStackOnline {
                return .green
            }
            if status.ipv4 || status.ipv6 || controller.portalResult == .offline {
                return .orange
            }
            return .secondary
        }
        return controller.isBusy ? .accentColor : .secondary
    }
}

// MARK: - Subviews

private struct StackStatusCard: View {
    enum State {
        case online
        case offline
        case checking
        case unknown
    }

    let title: String
    let state: State

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(title)
                    .font(.system(size: 13, weight: .semibold, design: .rounded))
                    .foregroundColor(.primary)

                Spacer()

                statusIcon
            }

            Text(subtitle)
                .font(.system(size: 11.5, weight: .medium))
                .foregroundColor(subtitleColor)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(cardFill, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(borderGradient, lineWidth: 0.6)
        )
    }

    @ViewBuilder
    private var statusIcon: some View {
        switch state {
        case .online:
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(.green)
        case .offline:
            Image(systemName: "exclamationmark.circle.fill")
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(.orange)
        case .checking:
            ProgressView()
                .controlSize(.small)
                .frame(width: 14, height: 14)
        case .unknown:
            Image(systemName: "circle.dashed")
                .font(.system(size: 14, weight: .medium))
                .foregroundColor(.secondary)
        }
    }

    private var subtitle: String {
        switch state {
        case .online: return "公网可达"
        case .offline: return "未连通"
        case .checking: return "检测中…"
        case .unknown: return "待检测"
        }
    }

    private var subtitleColor: Color {
        switch state {
        case .online: return .green
        case .offline: return .orange
        case .checking, .unknown: return .secondary
        }
    }

    private var cardFill: Color {
        switch state {
        case .online: return Color.green.opacity(0.11)
        case .offline: return Color.orange.opacity(0.11)
        case .checking, .unknown: return Color.primary.opacity(0.055)
        }
    }

    private var borderGradient: LinearGradient {
        let topColor: Color
        let bottomColor: Color
        switch state {
        case .online:
            topColor = Color.green.opacity(0.32)
            bottomColor = Color.green.opacity(0.14)
        case .offline:
            topColor = Color.orange.opacity(0.32)
            bottomColor = Color.orange.opacity(0.14)
        case .checking, .unknown:
            topColor = Color.white.opacity(0.24)
            bottomColor = Color.primary.opacity(0.08)
        }
        return LinearGradient(colors: [topColor, bottomColor], startPoint: .top, endPoint: .bottom)
    }
}

private struct QuotaCardView: View {
    let quota: CampusQuota
    let cachedTimeText: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 6) {
                Image(systemName: "chart.bar.fill")
                    .font(.system(size: 10.5, weight: .semibold))
                    .foregroundColor(.secondary)
                Text("校园网额度")
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundColor(.secondary)

                Spacer()

                if let accountID = quota.accountID {
                    Text(accountID)
                        .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                        .foregroundColor(.secondary)
                        .padding(.horizontal, 7)
                        .padding(.vertical, 2)
                        .background(Color.primary.opacity(0.06), in: Capsule())
                }
            }

            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("剩余流量")
                        .font(.system(size: 10.5))
                        .foregroundColor(.secondary)
                    Text(quota.remainingText)
                        .font(.system(size: 17.5, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundColor(quota.isLowRemainingFlow ? .orange : .primary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Rectangle()
                    .fill(Color.primary.opacity(0.1))
                    .frame(width: 1, height: 28)
                    .padding(.horizontal, 10)

                VStack(alignment: .leading, spacing: 2) {
                    Text("账户余额")
                        .font(.system(size: 10.5))
                        .foregroundColor(.secondary)
                    Text(quota.balanceText)
                        .font(.system(size: 17.5, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundColor(quota.isLowBalance ? .orange : .primary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            if showBottomSection {
                Rectangle()
                    .fill(Color.primary.opacity(0.07))
                    .frame(height: 0.5)

                if showDetailsRow {
                    HStack {
                        if let usedText = quota.usedText {
                            Text("累计已用 \(usedText)")
                        }
                        Spacer()
                        if let ipv6UsedText = quota.ipv6UsedText, (quota.ipv6UsedKB ?? 0) > 0 {
                            Text("IPv6 免计 \(ipv6UsedText)")
                        }
                    }
                    .font(.system(size: 10.5))
                    .monospacedDigit()
                    .foregroundColor(.secondary)
                }

                if let cachedTimeText {
                    Text("网关暂不可达 · 上次同步 \(cachedTimeText)")
                        .font(.system(size: 10))
                        .foregroundColor(.secondary)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.055), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(
                    LinearGradient(
                        colors: [Color.white.opacity(0.24), Color.primary.opacity(0.08)],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: 0.6
                )
        )
    }

    private var showDetailsRow: Bool {
        quota.usedText != nil || (quota.ipv6UsedKB ?? 0) > 0
    }

    private var showBottomSection: Bool {
        showDetailsRow || cachedTimeText != nil
    }
}

private struct QuotaUnavailableCard: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Image(systemName: "info.circle")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(.secondary)
                Text("校园网额度暂不可读取")
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundColor(.secondary)
            }
            Text("当前无法直连 192.168.100.200，若开启了 VPN/TUN 请放行网关，或检查「本地网络」权限。")
                .font(.system(size: 10.5))
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(
                    LinearGradient(
                        colors: [Color.white.opacity(0.22), Color.primary.opacity(0.08)],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: 0.6
                )
        )
    }
}

@MainActor
private final class HoverState: ObservableObject {
    @Published var isHovered = false
}

private struct HeaderIconButton: View {
    let systemName: String
    let helpText: String
    let action: () -> Void

    @StateObject private var hover = HoverState()

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 11.5, weight: .semibold))
                .foregroundColor(hover.isHovered ? .primary : .secondary)
                .frame(width: 28, height: 28)
                .background(
                    Circle()
                        .fill(Color.primary.opacity(hover.isHovered ? 0.11 : 0.055))
                )
                .overlay(
                    Circle()
                        .strokeBorder(
                            LinearGradient(
                                colors: [Color.white.opacity(0.28), Color.primary.opacity(0.08)],
                                startPoint: .top,
                                endPoint: .bottom
                            ),
                            lineWidth: 0.5
                        )
                )
        }
        .buttonStyle(.plain)
        .help(helpText)
        .onHover { hover.isHovered = $0 }
    }
}

private struct FooterActionButton: View {
    let title: String
    let systemImage: String?
    let action: () -> Void

    @StateObject private var hover = HoverState()

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let systemImage {
                    Image(systemName: systemImage)
                        .font(.system(size: 11))
                }
                Text(title)
                    .font(.system(size: 11.5, weight: .medium))
            }
            .foregroundColor(hover.isHovered ? .primary : .secondary)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(
                Capsule(style: .continuous)
                    .fill(Color.primary.opacity(hover.isHovered ? 0.09 : 0))
            )
        }
        .buttonStyle(.plain)
        .onHover { hover.isHovered = $0 }
    }
}

private struct PrimaryActionButtonStyle: ButtonStyle {
    enum Kind {
        case accent
        case online
        case busy
    }

    let style: Kind

    func makeBody(configuration: Configuration) -> some View {
        PrimaryActionButtonBody(style: style, isPressed: configuration.isPressed) {
            configuration.label
        }
    }

    private struct PrimaryActionButtonBody<Label: View>: View {
        let style: Kind
        let isPressed: Bool
        @ViewBuilder let label: () -> Label
        @StateObject private var hover = HoverState()

        var body: some View {
            label()
                .frame(maxWidth: .infinity)
                .frame(height: 38)
                .foregroundColor(foregroundColor)
                .background(backgroundView)
                .overlay(borderView)
                .scaleEffect(isPressed ? 0.985 : 1.0)
                .animation(.easeOut(duration: 0.12), value: isPressed)
                .onHover { hover.isHovered = $0 }
        }

        private var foregroundColor: Color {
            switch style {
            case .accent: return .white
            case .online: return .green
            case .busy: return .primary
            }
        }

        @ViewBuilder
        private var backgroundView: some View {
            let shape = Capsule(style: .continuous)
            switch style {
            case .accent:
                shape.fill(
                    LinearGradient(
                        colors: [
                            Color.accentColor.opacity(isPressed ? 0.88 : (hover.isHovered ? 0.95 : 1.0)),
                            Color.accentColor.opacity(isPressed ? 0.78 : (hover.isHovered ? 0.85 : 0.88)),
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
            case .online:
                shape.fill(Color.green.opacity(isPressed ? 0.22 : (hover.isHovered ? 0.18 : 0.14)))
            case .busy:
                shape.fill(Color.accentColor.opacity(isPressed ? 0.18 : (hover.isHovered ? 0.15 : 0.11)))
            }
        }

        @ViewBuilder
        private var borderView: some View {
            let shape = Capsule(style: .continuous)
            switch style {
            case .accent:
                shape.strokeBorder(
                    LinearGradient(
                        colors: [Color.white.opacity(0.28), Color.white.opacity(0.08)],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: 0.6
                )
            case .online:
                shape.strokeBorder(
                    LinearGradient(
                        colors: [Color.green.opacity(0.36), Color.green.opacity(0.16)],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: 0.6
                )
            case .busy:
                shape.strokeBorder(Color.accentColor.opacity(0.24), lineWidth: 0.6)
            }
        }
    }
}
