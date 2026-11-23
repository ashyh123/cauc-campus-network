import Foundation
import Testing
@testable import CAUCCampusCore

@Suite("AuthFlowEngine Tests")
struct AuthFlowEngineTests {
    @Test("Skips portal when already dual-stack online")
    func skipsPortalWhenAlreadyOnline() async {
        let recorder = EventRecorder()
        let quota = CampusQuota(accountID: "202400001", balanceYuan: 25.98, remainingKB: 53_227_520)

        let services = FlowServices(
            check: { _ in ConnectivityStatus(ipv4: true, ipv6: true) },
            fetchQuota: { .online(quota) }
        )

        await runNetworkFlow(authenticate: true, services: services) { event in
            await recorder.append(event)
        }

        let events = await recorder.snapshot()
        #expect(events.count == 1)
        #expect(events[0].done == true)
        #expect(events[0].openPortal == false)
        #expect(events[0].closePortal == true)
        #expect(events[0].triggerIPv6 == false)
        #expect(events[0].status == ConnectivityStatus(ipv4: true, ipv6: true))
        #expect(events[0].portalResult == .online(quota))
    }

    @Test("Opens portal, triggers IPv6 once, enables fallback, and closes portal on success")
    func authenticatesAndTriggersIPv6Once() async {
        let tracker = ProbeTracker(sequence: [
            ConnectivityStatus(ipv4: false, ipv6: false),
            ConnectivityStatus(ipv4: true, ipv6: false),
            ConnectivityStatus(ipv4: true, ipv6: false),
            ConnectivityStatus(ipv4: true, ipv6: true),
        ])
        let recorder = EventRecorder()
        let finalQuota = CampusQuota(accountID: "202400001", balanceYuan: 20.0, remainingKB: 10_485_760)

        let services = FlowServices(
            check: { fallback in
                await tracker.next(fallback: fallback)
            },
            fetchQuota: {
                let count = await tracker.quotaCallIncrement()
                return count == 1 ? .offline : .online(finalQuota)
            },
            pollInterval: .milliseconds(2),
            timeout: .seconds(2)
        )

        await runNetworkFlow(authenticate: true, services: services) { event in
            await recorder.append(event)
        }

        let events = await recorder.snapshot()
        let fallbacks = await tracker.recordedFallbacks()
        let opens = events.filter(\.openPortal).count
        let closes = events.filter(\.closePortal).count
        let triggers = events.filter(\.triggerIPv6).count

        #expect(fallbacks == [false, false, true, true])
        #expect(opens == 1)
        #expect(triggers == 1)
        #expect(closes == 1)
        #expect(events.last?.done == true)
        #expect(events.last?.status == ConnectivityStatus(ipv4: true, ipv6: true))
        #expect(events.last?.portalResult == .online(finalQuota))
    }

    @Test("Refresh with offline campus portal reports pending login message")
    func refreshWithOfflineCampusPortal() async {
        let recorder = EventRecorder()
        let services = FlowServices(
            check: { _ in ConnectivityStatus(ipv4: false, ipv6: false) },
            fetchQuota: { .offline }
        )

        await runNetworkFlow(authenticate: false, services: services) { event in
            await recorder.append(event)
        }

        let events = await recorder.snapshot()
        #expect(events.count == 1)
        #expect(events[0].openPortal == false)
        #expect(events[0].done == true)
        #expect(events[0].portalResult == .offline)
        #expect(events[0].message == "已连接校园网，待认证登录")
    }
}

private actor EventRecorder {
    private var events: [FlowEvent] = []

    func append(_ event: FlowEvent) {
        events.append(event)
    }

    func snapshot() -> [FlowEvent] {
        events
    }
}

private actor ProbeTracker {
    private let sequence: [ConnectivityStatus]
    private var index = 0
    private var fallbacks: [Bool] = []
    private var quotaCalls = 0

    init(sequence: [ConnectivityStatus]) {
        self.sequence = sequence
    }

    func next(fallback: Bool) -> ConnectivityStatus {
        fallbacks.append(fallback)
        let current = sequence[min(index, sequence.count - 1)]
        index += 1
        return current
    }

    func quotaCallIncrement() -> Int {
        quotaCalls += 1
        return quotaCalls
    }

    func recordedFallbacks() -> [Bool] {
        fallbacks
    }
}
