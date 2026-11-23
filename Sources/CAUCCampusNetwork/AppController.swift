import AppKit
import CAUCCampusCore
import Foundation
import Network

@MainActor
final class AppController: ObservableObject {
    private static let cachedQuotaKey = "local.cauc.campus-network.cachedQuota"

    @Published private(set) var connectivity: ConnectivityStatus?
    @Published private(set) var portalResult: PortalQueryResult = .unreachable
    @Published private(set) var cachedQuota: CachedCampusQuota?
    @Published private(set) var message: String = "正在检测网络状态…"
    @Published private(set) var isBusy: Bool = false
    @Published private(set) var isAuthenticating: Bool = false
    @Published private(set) var lastCheckedTime: String?

    private let prober = ConnectivityProber()
    private let quotaClient = CampusQuotaClient()
    private lazy var quotaCoordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) { [quotaClient] in
        await quotaClient.fetch()
    }
    private let portalController = PortalWindowController()
    private let pathMonitor = NWPathMonitor()
    private let pathMonitorQueue = DispatchQueue(label: "local.cauc.campus-network.path-monitor")
    private var currentPathSignature: String = "initial"
    private var hasReceivedInitialPath = false
    private var pathDebounceTask: Task<Void, Never>?
    private var flowTask: Task<Void, Never>?
    private var generation: UInt64 = 0
    private var lastRefreshDate: Date?
    nonisolated(unsafe) private var wakeObserver: NSObjectProtocol?

    private let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter
    }()

    private let todayCacheFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter
    }()

    private let olderCacheFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "MM-dd HH:mm"
        return formatter
    }()

    init() {
        if let data = UserDefaults.standard.data(forKey: Self.cachedQuotaKey),
           let decoded = try? JSONDecoder().decode(CachedCampusQuota.self, from: data),
           Date().timeIntervalSince(decoded.updatedAt) <= 7 * 24 * 3600
        {
            self.cachedQuota = decoded
        } else {
            UserDefaults.standard.removeObject(forKey: Self.cachedQuotaKey)
        }

        portalController.onUserClosedPortal = { [weak self] in
            self?.handlePortalClosedByUser()
        }

        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.refresh(force: true)
            }
        }
        startPathMonitoring()
        refresh(force: true)
    }

    deinit {
        if let wakeObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver)
        }
        pathMonitor.pathUpdateHandler = nil
        pathMonitor.cancel()
        pathDebounceTask?.cancel()
        flowTask?.cancel()
    }

    var quota: CampusQuota? {
        if case .online(let value) = portalResult {
            return value
        }
        return nil
    }

    var displayedQuota: CampusQuota? {
        quota ?? cachedQuota?.quota
    }

    var isShowingCachedQuota: Bool {
        portalResult == .unreachable && cachedQuota != nil
    }

    var cachedQuotaTimeText: String? {
        guard let updatedAt = cachedQuota?.updatedAt else {
            return nil
        }
        if Calendar.current.isDateInToday(updatedAt) {
            return todayCacheFormatter.string(from: updatedAt)
        }
        return olderCacheFormatter.string(from: updatedAt)
    }

    var showQuotaUnreachableHint: Bool {
        portalResult == .unreachable && cachedQuota == nil && !isBusy && connectivity != nil
    }

    var menuBarSymbolName: String {
        if isBusy && connectivity == nil {
            return "arrow.triangle.2.circlepath"
        }
        if connectivity?.isDualStackOnline == true {
            return "globe"
        }
        if connectivity?.ipv4 == true || connectivity?.ipv6 == true {
            return "network"
        }
        if connectivity != nil {
            return "wifi.exclamationmark"
        }
        return "network"
    }

    func handlePopoverOpened(launchAtLogin: LaunchAtLoginManager) {
        launchAtLogin.refresh()
        guard !isBusy else { return }
        if let lastRefreshDate, Date().timeIntervalSince(lastRefreshDate) < 3.0 {
            return
        }
        refresh(force: true, forceQuota: false)
    }

    func refresh(force: Bool = true, forceQuota: Bool? = nil) {
        if !force, isBusy {
            return
        }
        let resolvedForceQuota = forceQuota ?? force
        startFlow(authenticate: false, forceQuota: resolvedForceQuota)
    }

    func startAuthentication() {
        guard !isBusy else { return }
        startFlow(authenticate: true, forceQuota: true)
    }

    func stopChecking() {
        cancelActiveFlow(message: "已停止检测")
    }

    func openPortalManually() {
        portalController.openPortal()
    }

    func openWiFiSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.wifi-settings-extension") else {
            return
        }
        NSWorkspace.shared.open(url)
    }

    func quit() {
        NSApp.terminate(nil)
    }

    private func handlePortalClosedByUser() {
        guard isAuthenticating else { return }
        cancelActiveFlow(message: "已关闭认证窗口")
    }

    private func cancelActiveFlow(message: String) {
        pathDebounceTask?.cancel()
        pathDebounceTask = nil
        flowTask?.cancel()
        flowTask = nil
        generation &+= 1
        portalController.stopTrigger()
        isBusy = false
        isAuthenticating = false
        self.message = message
    }

    private func startPathMonitoring() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let newSignature = Self.signature(for: path)
            Task { @MainActor [weak self] in
                self?.handlePathUpdate(signature: newSignature)
            }
        }
        pathMonitor.start(queue: pathMonitorQueue)
    }

    private func handlePathUpdate(signature newSignature: String) {
        if !hasReceivedInitialPath {
            hasReceivedInitialPath = true
            currentPathSignature = newSignature
            return
        }

        guard newSignature != currentPathSignature else {
            return
        }
        currentPathSignature = newSignature

        pathDebounceTask?.cancel()
        pathDebounceTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(1500))
            guard let self, !Task.isCancelled else { return }
            self.pathDebounceTask = nil
            guard !self.isAuthenticating else { return }
            self.refresh(force: true, forceQuota: false)
        }
    }

    nonisolated private static func signature(for path: NWPath) -> String {
        let statusPart: String
        switch path.status {
        case .satisfied:
            statusPart = "satisfied"
        case .unsatisfied:
            statusPart = "unsatisfied"
        case .requiresConnection:
            statusPart = "requiresConnection"
        @unknown default:
            statusPart = "unknown"
        }

        func sortedNames(matching predicate: (NWInterface) -> Bool) -> String {
            Set(path.availableInterfaces.filter(predicate).map(\.name))
                .sorted()
                .joined(separator: ",")
        }

        let physical = sortedNames { $0.type == .wifi || $0.type == .wiredEthernet }
        let usesOther = path.usesInterfaceType(.other)
        let other = sortedNames { $0.type == .other }

        return "\(statusPart)|phys:\(physical)|other:\(usesOther):\(other)"
    }

    private func startFlow(authenticate: Bool, forceQuota: Bool) {
        pathDebounceTask?.cancel()
        pathDebounceTask = nil
        flowTask?.cancel()
        portalController.stopTrigger()

        generation &+= 1
        let activeGeneration = generation
        lastRefreshDate = Date()
        isBusy = true
        isAuthenticating = authenticate
        message = authenticate ? "正在检查当前网络并准备认证…" : "正在检测 IPv4 与 IPv6…"

        let prober = self.prober
        let quotaCoordinator = self.quotaCoordinator
        let pathSignature = currentPathSignature
        let useForce = authenticate || forceQuota
        let publishEvent: @MainActor @Sendable (FlowEvent) -> Void = { [weak self] event in
            self?.apply(event: event, generation: activeGeneration)
        }

        flowTask = Task {
            let services = FlowServices(
                check: { fallback in
                    await prober.check(ustcFallback: fallback)
                },
                fetchQuota: {
                    await quotaCoordinator.fetch(
                        mode: useForce ? .force(pathSignature: pathSignature) : .smart(pathSignature: pathSignature)
                    )
                },
                pollInterval: .seconds(2),
                timeout: .seconds(55)
            )

            await runNetworkFlow(authenticate: authenticate, services: services) { event in
                await publishEvent(event)
            }
        }
    }

    private func apply(event: FlowEvent, generation: UInt64) {
        guard generation == self.generation else { return }

        if let status = event.status {
            connectivity = status
            let now = Date()
            lastCheckedTime = timeFormatter.string(from: now)
            lastRefreshDate = now
        }
        if let portal = event.portalResult {
            portalResult = portal
            switch portal {
            case .online(let value):
                let cached = CachedCampusQuota(quota: value, updatedAt: Date())
                cachedQuota = cached
                if let encoded = try? JSONEncoder().encode(cached) {
                    UserDefaults.standard.set(encoded, forKey: Self.cachedQuotaKey)
                }
            case .offline:
                cachedQuota = nil
                UserDefaults.standard.removeObject(forKey: Self.cachedQuotaKey)
            case .unreachable:
                break
            }
        }
        if let updatedMessage = event.message {
            message = updatedMessage
        }
        if event.openPortal {
            portalController.openPortal()
        }
        if event.triggerIPv6 {
            portalController.triggerIPv6()
        }
        if event.closePortal {
            portalController.closePortal()
        }
        if event.done {
            isBusy = false
            isAuthenticating = false
            portalController.stopTrigger()
            flowTask = nil
        }
    }
}
