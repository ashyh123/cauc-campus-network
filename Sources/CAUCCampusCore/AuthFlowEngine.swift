import Foundation

public struct FlowEvent: Equatable, Sendable {
    public var status: ConnectivityStatus?
    public var portalResult: PortalQueryResult?
    public var message: String?
    public var openPortal: Bool
    public var closePortal: Bool
    public var triggerIPv6: Bool
    public var done: Bool

    public init(
        status: ConnectivityStatus? = nil,
        portalResult: PortalQueryResult? = nil,
        message: String? = nil,
        openPortal: Bool = false,
        closePortal: Bool = false,
        triggerIPv6: Bool = false,
        done: Bool = false
    ) {
        self.status = status
        self.portalResult = portalResult
        self.message = message
        self.openPortal = openPortal
        self.closePortal = closePortal
        self.triggerIPv6 = triggerIPv6
        self.done = done
    }
}

public struct FlowServices: Sendable {
    public var check: @Sendable (_ ustcFallback: Bool) async -> ConnectivityStatus
    public var fetchQuota: @Sendable () async -> PortalQueryResult
    public var pollInterval: Duration
    public var timeout: Duration

    public init(
        check: @escaping @Sendable (_ ustcFallback: Bool) async -> ConnectivityStatus,
        fetchQuota: @escaping @Sendable () async -> PortalQueryResult,
        pollInterval: Duration = .seconds(2),
        timeout: Duration = .seconds(55)
    ) {
        self.check = check
        self.fetchQuota = fetchQuota
        self.pollInterval = pollInterval
        self.timeout = timeout
    }
}

public func runNetworkFlow(
    authenticate: Bool,
    services: FlowServices,
    publish: @escaping @Sendable (FlowEvent) async -> Void
) async {
    async let initialCheck = services.check(false)
    async let initialQuota = services.fetchQuota()

    var currentStatus = await initialCheck
    let initialPortal = await initialQuota
    guard !Task.isCancelled else { return }

    if !authenticate || currentStatus.isDualStackOnline {
        let message = statusSummary(for: currentStatus, portalResult: initialPortal)
        await publish(FlowEvent(
            status: currentStatus,
            portalResult: initialPortal,
            message: message,
            closePortal: authenticate && currentStatus.isDualStackOnline,
            done: true
        ))
        return
    }

    await publish(FlowEvent(
        status: currentStatus,
        portalResult: initialPortal,
        message: "请在认证窗口完成登录",
        openPortal: true
    ))

    var triggered = false
    let deadline = ContinuousClock.now + services.timeout

    while !Task.isCancelled {
        if currentStatus.ipv4 && !currentStatus.ipv6 && !triggered {
            triggered = true
            await publish(FlowEvent(
                message: "IPv4 已连通，正在激活并复检 IPv6…",
                triggerIPv6: true
            ))
        }

        let remaining = ContinuousClock.now.duration(to: deadline)
        if remaining <= .zero {
            break
        }

        try? await Task.sleep(for: min(services.pollInterval, remaining))
        guard !Task.isCancelled else { return }

        if ContinuousClock.now >= deadline {
            break
        }

        let nextStatus = await services.check(triggered)
        guard !Task.isCancelled else { return }
        currentStatus = nextStatus

        if currentStatus.isDualStackOnline {
            let freshQuota = await services.fetchQuota()
            guard !Task.isCancelled else { return }
            await publish(FlowEvent(
                status: currentStatus,
                portalResult: freshQuota,
                message: "认证完成，IPv4 和 IPv6 均可访问公网",
                closePortal: true,
                done: true
            ))
            return
        }

        await publish(FlowEvent(status: currentStatus))
    }

    guard !Task.isCancelled else { return }

    let freshQuota = await services.fetchQuota()
    guard !Task.isCancelled else { return }

    let timeoutMessage = (currentStatus.ipv4 && !currentStatus.ipv6)
        ? "IPv4 已连通，IPv6 仍未通过探测"
        : "等待认证超时，请确认登录结果"

    await publish(FlowEvent(
        status: currentStatus,
        portalResult: freshQuota,
        message: timeoutMessage,
        done: true
    ))
}

private func statusSummary(for status: ConnectivityStatus, portalResult: PortalQueryResult) -> String {
    switch (status.ipv4, status.ipv6) {
    case (true, true):
        return "IPv4 和 IPv6 均可访问公网"
    case (true, false):
        return "IPv4 已连通，IPv6 暂不可达"
    case (false, true):
        return "仅 IPv6 可访问公网"
    case (false, false):
        return portalResult == .offline ? "已连接校园网，待认证登录" : "暂未连通公网"
    }
}
