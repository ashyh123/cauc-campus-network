import Foundation
import Testing
@testable import CAUCCampusCore

@Suite("CampusQuotaParser Tests")
struct CampusQuotaParserTests {
    @Test("Parses real CAUC dr1002 payload with duplicate key and missing trailing parenthesis")
    func parsesRealCAUCPayload() {
        let raw = #" dr1002({"result":1,"time":33287,"flow":212418672,"fsele":1,"fee":259800,"m46":0,"v46ip":"10.0.0.100","myv6ip":"","oltime":4294967295,"olflow":53227520,"lip":"10.0.0.100","stime":"2026-11-21 19:47:48","etime":"2026-11-21 20:17:54","uid":"202400001","v6af":185273559,"v6df":148855911,"v46m":4,"v4ip":"10.0.0.100","v6ip":"2001:db8::1","AC":"202400001","ss5":"10.0.0.100","ss6":"192.168.100.200","vid":0,"ss1":"020000000002","ss4":"000000000000","cvid":0,"pvid":0,"hotel":0,"aolno":15701,"eport":-1,"eclass":1,"zxopt":1,"NID":"?????","olno":1,"udate":"","olmac":"020000000001","ollm":10,"olm1":"00000014","olm2":"4210","olm3":0,"olmm":1,"olm5":0,"gid":4,"ispid":0,"opip":"0.0.0.0","olno":1,"olip":"10.0.0.200","oaf":2855202,"oat":25636,"actM":1,"actt":9833,"actdf":2766037,"actuf":0,"act6df":76535,"act6uf":10112,"allfm":1,"d1":0,"u1":0,"d2":0,"u2":0,"o1":0,"nd1":11064151,"nu1":2875006,"nd2":306143,"nu2":40448,"no1":0}"#
        let parsed = CampusQuotaParser.parse(Data(raw.utf8))

        guard case .online(let quota) = parsed else {
            Issue.record("Expected .online result, got \(String(describing: parsed))")
            return
        }

        #expect(quota.accountID == "202400001")
        #expect(quota.balanceYuan == 25.98)
        #expect(quota.balanceText == "¥25.98")
        #expect(quota.remainingKB == 53_227_520)
        #expect(quota.isUnlimitedFlow == false)
        #expect(quota.remainingText == "50.76 GB")
        #expect(quota.usedKB == 212_418_672)
        #expect(quota.usedText == "202.58 GB")
        #expect(quota.ipv6UsedKB == 185_273_559)
        #expect(quota.ipv6UsedText == "176.69 GB")
        #expect(quota.ipv4Address == "10.0.0.100")
        #expect(quota.ipv6Address == "2001:db8::1")
    }

    @Test("Survives invalid UTF-8 / raw GBK bytes inside NID field")
    func parsesPayloadWithInvalidUTF8Bytes() {
        var data = Data(#"dr1002({"result":1,"uid":"202400001","fee":125055,"olflow":1048576,"NID":""#.utf8)
        // Raw GBK bytes for "张三" (invalid UTF-8)
        data.append(contentsOf: [0xD5, 0xC5, 0xC8, 0xFD])
        data.append(Data(#""});"#.utf8))

        let parsed = CampusQuotaParser.parse(data)
        guard case .online(let quota) = parsed else {
            Issue.record("Expected .online result despite GBK bytes")
            return
        }

        #expect(quota.accountID == "202400001")
        #expect(quota.balanceYuan == 12.50)
        #expect(quota.balanceText == "¥12.50")
        #expect(quota.remainingKB == 1_048_576)
        #expect(quota.remainingText == "1.00 GB")
    }

    @Test("Parses unauthenticated offline Dr.COM status")
    func parsesOfflinePayload() {
        let raw = #"dr1002({"result":0,"v46ip":"10.0.0.100"});"#
        let parsed = CampusQuotaParser.parse(Data(raw.utf8))
        #expect(parsed == .offline)
    }

    @Test("Handles UInt32.max olflow as unlimited flow")
    func parsesUnlimitedFlow() {
        let raw = #"dr1002({"result":1,"uid":"202400001","fee":50000,"olflow":4294967295});"#
        let parsed = CampusQuotaParser.parse(Data(raw.utf8))

        guard case .online(let quota) = parsed else {
            Issue.record("Expected .online result")
            return
        }

        #expect(quota.isUnlimitedFlow == true)
        #expect(quota.remainingKB == nil)
        #expect(quota.remainingText == "不限量")
        #expect(quota.balanceText == "¥5.00")
    }

    @Test("Returns nil for malformed or non-JSON bodies")
    func rejectsMalformedBody() {
        #expect(CampusQuotaParser.parse(Data("<html>not json</html>".utf8)) == nil)
        #expect(CampusQuotaParser.parse(Data("dr1002({invalid})".utf8)) == nil)
        #expect(CampusQuotaParser.parse(Data()) == nil)
    }

    @Test("Formats traffic across KB, MB, GB thresholds and currency")
    func formatsUnitsCorrectly() {
        #expect(CampusQuota.formatTraffic(kb: 512) == "512 KB")
        #expect(CampusQuota.formatTraffic(kb: 2048) == "2.0 MB")
        #expect(CampusQuota.formatTraffic(kb: 1_572_864) == "1.50 GB")
        #expect(CampusQuota.formatCurrency(12.5) == "¥12.50")
    }

    @Test("Parses raw HTTP 200 response containing CAUC JSONP payload")
    func parsesHTTP200Response() {
        let body = #"dr1002({"result":1,"uid":"202400001","fee":259800,"olflow":53227520,"flow":212418672,"v6af":185273559});"#
        let rawHTTP = "HTTP/1.1 200 OK\r\nContent-Type: text/javascript; charset=gbk\r\nConnection: close\r\n\r\n\(body)"
        let parsed = CampusQuotaParser.parseHTTPResponse(Data(rawHTTP.utf8))

        guard case .online(let quota) = parsed else {
            Issue.record("Expected .online from HTTP 200 response, got \(String(describing: parsed))")
            return
        }

        #expect(quota.accountID == "202400001")
        #expect(quota.balanceText == "¥25.98")
        #expect(quota.remainingText == "50.76 GB")
    }

    @Test("Rejects non-2xx HTTP status codes in raw HTTP responses")
    func rejectsNon2xxHTTPResponses() {
        let redirectHTTP = "HTTP/1.1 302 Found\r\nLocation: http://192.168.100.200/\r\n\r\n{\"result\":1}"
        let notFoundHTTP = "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\n\r\n{\"result\":1}"

        #expect(CampusQuotaParser.parseHTTPResponse(Data(redirectHTTP.utf8)) == nil)
        #expect(CampusQuotaParser.parseHTTPResponse(Data(notFoundHTTP.utf8)) == nil)
    }

    @Test("CachedCampusQuota and CampusQuota round-trip through Codable")
    func roundTripsCachedCampusQuota() throws {
        let quota = CampusQuota(
            accountID: "202400001",
            balanceYuan: 25.98,
            remainingKB: 53_227_520,
            isUnlimitedFlow: false,
            usedKB: 212_418_672,
            ipv6UsedKB: 185_273_559,
            onlineMinutes: 33_287,
            ipv4Address: "10.0.0.100",
            ipv6Address: "2001:db8::1"
        )
        let original = CachedCampusQuota(quota: quota, updatedAt: Date(timeIntervalSince1970: 1_795_273_851))

        let encoded = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(CachedCampusQuota.self, from: encoded)
        #expect(decoded == original)
    }

    @Test("Parses raw HTTP response using LF-only header separator")
    func parsesHTTPResponseWithLFOnlySeparator() {
        let rawHTTP = "HTTP/1.1 200 OK\nContent-Type: text/javascript\n\ndr1002({\"result\":0});"
        #expect(CampusQuotaParser.parseHTTPResponse(Data(rawHTTP.utf8)) == .offline)
    }
}

@Suite("QuotaFetchCoordinator Tests")
struct QuotaFetchCoordinatorTests {
    @Test("Smart mode reuses online quota within freshTTL and refetches once freshTTL elapses")
    func smartReusesOnlineWithinFreshTTLAndRefetchesAfterExpiry() async {
        let q1 = CampusQuota(accountID: "202400001", balanceYuan: 25.0, remainingKB: 1_048_576)
        let q2 = CampusQuota(accountID: "202400001", balanceYuan: 24.5, remainingKB: 1_000_000)
        let recorder = QuotaFetchRecorder(results: [.online(q1), .online(q2)])
        let coordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) {
            await recorder.fetch()
        }

        let t0 = Date(timeIntervalSince1970: 1_000_000)
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0) == .online(q1))
        #expect(await recorder.callCount == 1)

        // Still fresh (< 60s)
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(59)) == .online(q1))
        #expect(await recorder.callCount == 1)

        // Exact TTL elapsed (>= 60s) triggers refetch
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(60)) == .online(q2))
        #expect(await recorder.callCount == 2)

        // Newly fetched online result is reused within the new TTL window
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(90)) == .online(q2))
        #expect(await recorder.callCount == 2)
    }

    @Test("Smart mode suppresses unreachable within cooldown on same path but refetches on path change or force")
    func smartUnreachableCooldownPathChangeAndForce() async {
        let qOnline = CampusQuota(accountID: "202400001", balanceYuan: 10.0, remainingKB: 524_288)
        let qUpdated = CampusQuota(accountID: "202400001", balanceYuan: 9.5, remainingKB: 500_000)
        let recorder = QuotaFetchRecorder(results: [.unreachable, .unreachable, .online(qOnline), .online(qUpdated)])
        let coordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) {
            await recorder.fetch()
        }

        let t0 = Date(timeIntervalSince1970: 2_000_000)
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "tun:utun4|wifi:en0"), now: t0) == .unreachable)
        #expect(await recorder.callCount == 1)

        // Same pathSignature within cooldown -> suppressed
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "tun:utun4|wifi:en0"), now: t0.addingTimeInterval(30)) == .unreachable)
        #expect(await recorder.callCount == 1)

        // Changed pathSignature -> refetches immediately even within cooldown window
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(31)) == .unreachable)
        #expect(await recorder.callCount == 2)

        // Same pathSignature but .force -> refetches immediately
        #expect(await coordinator.fetch(mode: .force(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(35)) == .online(qOnline))
        #expect(await recorder.callCount == 3)

        // .force also bypasses fresh online cache
        #expect(await coordinator.fetch(mode: .force(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(40)) == .online(qUpdated))
        #expect(await recorder.callCount == 4)
    }

    @Test("Smart mode does not suppress offline re-checks")
    func smartDoesNotSuppressOfflineRechecks() async {
        let qOnline = CampusQuota(accountID: "202400001", balanceYuan: 15.0, remainingKB: 2_097_152)
        let recorder = QuotaFetchRecorder(results: [.offline, .offline, .online(qOnline)])
        let coordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) {
            await recorder.fetch()
        }

        let t0 = Date(timeIntervalSince1970: 3_000_000)
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0) == .offline)
        #expect(await recorder.callCount == 1)

        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(1)) == .offline)
        #expect(await recorder.callCount == 2)

        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(2)) == .online(qOnline))
        #expect(await recorder.callCount == 3)
    }

    @Test("Invalidate clears cached online and unreachable state including across in-flight fetch")
    func invalidateClearsStoredAndInFlightState() async {
        let q1 = CampusQuota(accountID: "202400001", balanceYuan: 20.0, remainingKB: 1_048_576)
        let q2 = CampusQuota(accountID: "202400001", balanceYuan: 19.0, remainingKB: 1_000_000)
        let recorder = QuotaFetchRecorder(results: [.online(q1), .unreachable, .online(q2)])
        let coordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) {
            await recorder.fetch()
        }

        let t0 = Date(timeIntervalSince1970: 4_000_000)
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0) == .online(q1))
        #expect(await recorder.callCount == 1)

        await coordinator.invalidate()
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(1)) == .unreachable)
        #expect(await recorder.callCount == 2)

        await coordinator.invalidate()
        #expect(await coordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(2)) == .online(q2))
        #expect(await recorder.callCount == 3)

        // Verify invalidate during an in-flight fetch prevents stale result caching
        let gate = FetchGate()
        let gatedRecorder = QuotaFetchRecorder(results: [.online(q1), .online(q2)])
        let gatedCoordinator = QuotaFetchCoordinator(freshTTL: 60, unreachableCooldown: 60) {
            let result = await gatedRecorder.fetch()
            if result == .online(q1) {
                await gate.enterAndWaitForRelease()
            }
            return result
        }

        let firstTask = Task {
            await gatedCoordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0)
        }
        await gate.waitUntilEntered()
        await gatedCoordinator.invalidate()
        await gate.release()

        #expect(await firstTask.value == .online(q1))
        #expect(await gatedCoordinator.fetch(mode: .smart(pathSignature: "wifi:en0"), now: t0.addingTimeInterval(1)) == .online(q2))
        #expect(await gatedRecorder.callCount == 2)
    }
}

private actor QuotaFetchRecorder {
    private let results: [PortalQueryResult]
    private var index = 0
    private(set) var callCount = 0

    init(results: [PortalQueryResult]) {
        self.results = results
    }

    func fetch() -> PortalQueryResult {
        callCount += 1
        guard !results.isEmpty else { return .unreachable }
        let value = results[min(index, results.count - 1)]
        index += 1
        return value
    }
}

private actor FetchGate {
    private var enteredContinuation: CheckedContinuation<Void, Never>?
    private var releaseContinuation: CheckedContinuation<Void, Never>?
    private var hasEntered = false
    private var isReleased = false

    func waitUntilEntered() async {
        if hasEntered { return }
        await withCheckedContinuation { enteredContinuation = $0 }
    }

    func enterAndWaitForRelease() async {
        hasEntered = true
        enteredContinuation?.resume()
        enteredContinuation = nil
        if isReleased { return }
        await withCheckedContinuation { releaseContinuation = $0 }
    }

    func release() {
        isReleased = true
        releaseContinuation?.resume()
        releaseContinuation = nil
    }
}
