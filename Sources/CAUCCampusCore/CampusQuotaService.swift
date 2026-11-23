import Foundation
import Network

public enum CampusQuotaParser {
    private struct RawChkStatus: Decodable {
        let result: FlexibleInt
        let uid: String?
        let fee: Int64?
        let olflow: UInt64?
        let flow: UInt64?
        let v6af: UInt64?
        let time: UInt64?
        let v4ip: String?
        let v46ip: String?
        let v6ip: String?
    }

    private struct FlexibleInt: Decodable {
        let value: Int

        init(from decoder: Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let intVal = try? container.decode(Int.self) {
                value = intVal
            } else if let strVal = try? container.decode(String.self),
                      let parsed = Int(strVal.trimmingCharacters(in: .whitespacesAndNewlines))
            {
                value = parsed
            } else {
                throw DecodingError.typeMismatch(
                    Int.self,
                    DecodingError.Context(codingPath: decoder.codingPath, debugDescription: "Expected Int or numeric String")
                )
            }
        }
    }

    public static func parseHTTPResponse(_ rawHTTP: Data) -> PortalQueryResult? {
        let crlfSeparator = Data([0x0D, 0x0A, 0x0D, 0x0A])
        let lfSeparator = Data([0x0A, 0x0A])

        let separatorRange = rawHTTP.range(of: crlfSeparator) ?? rawHTTP.range(of: lfSeparator)
        guard let separatorRange else {
            return nil
        }

        let headerData = rawHTTP[..<separatorRange.lowerBound]
        let bodyData = rawHTTP[separatorRange.upperBound...]

        guard let headerText = String(data: headerData, encoding: .utf8) ?? String(data: headerData, encoding: .ascii),
              let firstLine = headerText.split(whereSeparator: \.isNewline).first
        else {
            return nil
        }

        let statusParts = firstLine.trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
        guard statusParts.count >= 2,
              statusParts[0].hasPrefix("HTTP/"),
              let statusCode = Int(statusParts[1]),
              (200..<300).contains(statusCode)
        else {
            return nil
        }

        return parse(Data(bodyData))
    }

    public static func parse(_ data: Data) -> PortalQueryResult? {
        // Decode lossily as UTF-8 so GBK bytes inside fields such as "NID" never fail decoding.
        let text = String(decoding: data, as: UTF8.self)
        guard let start = text.firstIndex(of: "{"),
              let end = text.lastIndex(of: "}"),
              start <= end
        else {
            return nil
        }

        let jsonSubstring = String(text[start...end])
        guard let jsonData = jsonSubstring.data(using: .utf8),
              let raw = try? JSONDecoder().decode(RawChkStatus.self, from: jsonData)
        else {
            return nil
        }

        switch raw.result.value {
        case 0:
            return .offline
        case 1:
            let balanceYuan: Double? = raw.fee.map { rawFee in
                if rawFee >= 0 {
                    return Double(rawFee - (rawFee % 100)) / 10_000.0
                }
                return Double(rawFee) / 10_000.0
            }
            let unlimited = raw.olflow == UInt64(UInt32.max)
            let remainingKB = unlimited ? nil : raw.olflow

            let quota = CampusQuota(
                accountID: nonEmpty(raw.uid),
                balanceYuan: balanceYuan,
                remainingKB: remainingKB,
                isUnlimitedFlow: unlimited,
                usedKB: raw.flow,
                ipv6UsedKB: raw.v6af,
                onlineMinutes: raw.time,
                ipv4Address: nonEmpty(raw.v4ip) ?? nonEmpty(raw.v46ip),
                ipv6Address: nonEmpty(raw.v6ip)
            )
            return .online(quota)
        default:
            return nil
        }
    }

    private static func nonEmpty(_ string: String?) -> String? {
        guard let trimmed = string?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty
        else {
            return nil
        }
        return trimmed
    }
}

public enum QuotaFetchMode: Equatable, Sendable {
    case smart(pathSignature: String)
    case force(pathSignature: String)
}

public actor QuotaFetchCoordinator {
    private let freshTTL: TimeInterval
    private let unreachableCooldown: TimeInterval
    private let fetcher: @Sendable () async -> PortalQueryResult

    private var lastPathSignature: String?
    private var lastResult: PortalQueryResult?
    private var lastFetchedAt: Date?
    private var generation: UInt64 = 0

    public init(
        freshTTL: TimeInterval = 60,
        unreachableCooldown: TimeInterval = 60,
        fetcher: @escaping @Sendable () async -> PortalQueryResult
    ) {
        self.freshTTL = freshTTL
        self.unreachableCooldown = unreachableCooldown
        self.fetcher = fetcher
    }

    public func fetch(mode: QuotaFetchMode, now: Date = Date()) async -> PortalQueryResult {
        let pathSignature: String
        switch mode {
        case .smart(let sig):
            pathSignature = sig
            if sig == lastPathSignature,
               let lastFetchedAt,
               let lastResult
            {
                let elapsed = now.timeIntervalSince(lastFetchedAt)
                switch lastResult {
                case .online:
                    if elapsed < freshTTL {
                        return lastResult
                    }
                case .unreachable:
                    if elapsed < unreachableCooldown {
                        return .unreachable
                    }
                case .offline:
                    break
                }
            }
        case .force(let sig):
            pathSignature = sig
        }

        generation &+= 1
        let fetchGeneration = generation
        let result = await fetcher()

        if generation == fetchGeneration, !(Task.isCancelled && result == .unreachable) {
            lastPathSignature = pathSignature
            lastResult = result
            lastFetchedAt = now
        }

        return result
    }

    public func invalidate() {
        generation &+= 1
        lastPathSignature = nil
        lastResult = nil
        lastFetchedAt = nil
    }
}

public struct CampusQuotaClient: Sendable {
    private final class InterfaceMemory: @unchecked Sendable {
        private let lock = NSLock()
        private var lastSuccessfulInterfaceName: String?

        func current() -> String? {
            lock.lock()
            defer { lock.unlock() }
            return lastSuccessfulInterfaceName
        }

        func record(_ name: String) {
            lock.lock()
            lastSuccessfulInterfaceName = name
            lock.unlock()
        }
    }

    private let session: URLSession
    private let interfaceMemory = InterfaceMemory()

    public init() {
        let config = URLSessionConfiguration.ephemeral
        // Bypass system proxies so local Dr.COM gateway 192.168.100.200 is queried directly.
        config.connectionProxyDictionary = [:]
        config.timeoutIntervalForRequest = 2.0
        config.timeoutIntervalForResource = 2.5
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.urlCache = nil
        self.session = URLSession(configuration: config)
    }

    public func fetch() async -> PortalQueryResult {
        if Task.isCancelled {
            return .unreachable
        }

        let token = Int.random(in: 1000...999_999)
        let path = "/drcom/chkstatus?callback=dr1002&jsVersion=4.1.3&v=\(token)&lang=zh"
        let urlString = "\(CampusEndpoints.chkstatusBase)?callback=dr1002&jsVersion=4.1.3&v=\(token)&lang=zh"

        let preferredInterfaceName = interfaceMemory.current()
        let collected = await PathInterfaceCollector().collect()
        if Task.isCancelled {
            return .unreachable
        }

        let interfaces: [InterfaceBox]
        if let preferredInterfaceName {
            let preferred = collected.filter { $0.value.name == preferredInterfaceName }
            let others = collected.filter { $0.value.name != preferredInterfaceName }
            interfaces = preferred + others
        } else {
            interfaces = collected
        }

        return await withTaskGroup(of: (PortalQueryResult, String?).self) { group in
            for box in interfaces {
                let interfaceName = box.value.name
                group.addTask {
                    let fetcher = DirectGatewayFetcher(interface: box.value, path: path, timeout: .seconds(2))
                    let result = await fetcher.fetch()
                    return (result, interfaceName)
                }
            }

            group.addTask {
                let result = await fetchViaURLSession(urlString: urlString)
                return (result, nil)
            }

            for await (result, interfaceName) in group {
                switch result {
                case .online, .offline:
                    if let interfaceName {
                        interfaceMemory.record(interfaceName)
                    }
                    group.cancelAll()
                    return result
                case .unreachable:
                    continue
                }
            }

            return .unreachable
        }
    }

    private func fetchViaURLSession(urlString: String) async -> PortalQueryResult {
        guard !Task.isCancelled, let url = URL(string: urlString) else {
            return .unreachable
        }

        var request = URLRequest(url: url)
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.timeoutInterval = 2.0

        guard let (data, response) = try? await session.data(for: request) else {
            return .unreachable
        }

        let isSuccessStatus = (response as? HTTPURLResponse).map { (200...299).contains($0.statusCode) } ?? true
        guard isSuccessStatus, let parsed = CampusQuotaParser.parse(data) else {
            return .unreachable
        }
        return parsed
    }
}

private struct InterfaceBox: @unchecked Sendable {
    let value: NWInterface
}

private final class PathInterfaceCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<[InterfaceBox], Never>?
    private var finished = false
    private var monitor: NWPathMonitor?
    private var timer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "local.cauc.campus-network.quota-path")

    func collect() async -> [InterfaceBox] {
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                start(continuation: continuation)
            }
        } onCancel: {
            finish([])
        }
    }

    private func start(continuation: CheckedContinuation<[InterfaceBox], Never>) {
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            var seen = Set<String>()
            var physical: [InterfaceBox] = []
            for iface in path.availableInterfaces where iface.type == .wifi || iface.type == .wiredEthernet {
                if seen.insert(iface.name).inserted {
                    physical.append(InterfaceBox(value: iface))
                }
            }
            self?.finish(physical)
        }

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + .milliseconds(300))
        timer.setEventHandler { [weak self] in
            self?.finish([])
        }

        lock.lock()
        let shouldStart = !finished
        if shouldStart {
            self.continuation = continuation
            self.monitor = monitor
            self.timer = timer
        }
        lock.unlock()

        guard shouldStart else {
            timer.cancel()
            timer.resume()
            monitor.pathUpdateHandler = nil
            monitor.cancel()
            continuation.resume(returning: [])
            return
        }

        timer.resume()
        monitor.start(queue: queue)
    }

    private func finish(_ result: [InterfaceBox]) {
        lock.lock()
        let cont: CheckedContinuation<[InterfaceBox], Never>?
        let activeMonitor: NWPathMonitor?
        let activeTimer: DispatchSourceTimer?
        if finished {
            cont = nil
            activeMonitor = nil
            activeTimer = nil
        } else {
            finished = true
            cont = continuation
            continuation = nil
            activeMonitor = monitor
            monitor = nil
            activeTimer = timer
            timer = nil
        }
        lock.unlock()

        activeTimer?.cancel()
        activeMonitor?.pathUpdateHandler = nil
        activeMonitor?.cancel()
        cont?.resume(returning: result)
    }
}

private final class DirectGatewayFetcher: @unchecked Sendable {
    private static let maxResponseBytes = 65_536
    private static let crlfSeparator = Data([0x0D, 0x0A, 0x0D, 0x0A])
    private static let lfSeparator = Data([0x0A, 0x0A])

    private struct State {
        var continuation: CheckedContinuation<PortalQueryResult, Never>?
        var finished = false
        var buffer = Data()
        var timer: DispatchSourceTimer?
        var connection: NWConnection?
    }

    private let lock = NSLock()
    private var state = State()
    private let interface: NWInterface
    private let requestData: Data
    private let timeout: Duration
    private let queue = DispatchQueue(label: "local.cauc.campus-network.quota-direct")

    init(interface: NWInterface, path: String, timeout: Duration) {
        self.interface = interface
        self.timeout = timeout
        let rawRequest = "GET \(path) HTTP/1.1\r\nHost: 192.168.100.200\r\nUser-Agent: CAUCCampusNetwork/2.0\r\nConnection: close\r\n\r\n"
        self.requestData = Data(rawRequest.utf8)
    }

    func fetch() async -> PortalQueryResult {
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                start(continuation: continuation)
            }
        } onCancel: {
            finish(.unreachable)
        }
    }

    private func start(continuation: CheckedContinuation<PortalQueryResult, Never>) {
        let parameters = NWParameters.tcp
        parameters.requiredInterface = interface
        if let ipOptions = parameters.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            ipOptions.version = .v4
        }

        let connection = NWConnection(
            host: NWEndpoint.Host("192.168.100.200"),
            port: NWEndpoint.Port(rawValue: 80)!,
            using: parameters
        )
        connection.stateUpdateHandler = { [weak self] newState in
            self?.handleState(newState, on: connection)
        }

        let timer = DispatchSource.makeTimerSource(queue: queue)
        let timeoutNanoseconds = max(1, timeout.components.seconds * 1_000_000_000 + timeout.components.attoseconds / 1_000_000_000)
        timer.schedule(deadline: .now() + .nanoseconds(Int(clamping: timeoutNanoseconds)))
        timer.setEventHandler { [weak self] in
            self?.finish(.unreachable)
        }

        lock.lock()
        let shouldStart = !state.finished
        if shouldStart {
            state.continuation = continuation
            state.connection = connection
            state.timer = timer
        }
        lock.unlock()

        guard shouldStart else {
            timer.cancel()
            timer.resume()
            connection.stateUpdateHandler = nil
            connection.cancel()
            continuation.resume(returning: .unreachable)
            return
        }

        timer.resume()
        connection.start(queue: queue)
    }

    private func handleState(_ newState: NWConnection.State, on connection: NWConnection) {
        switch newState {
        case .ready:
            connection.send(content: requestData, completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                if error != nil {
                    self.finish(.unreachable)
                    return
                }
                self.receiveNextChunk(on: connection)
            })
        case .waiting, .failed, .cancelled:
            finish(.unreachable)
        case .setup, .preparing:
            break
        @unknown default:
            break
        }
    }

    private func receiveNextChunk(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, isComplete, error in
            guard let self else { return }

            var terminalSnapshot: Data?
            var incrementalSnapshot: Data?

            self.lock.lock()
            if self.state.finished {
                self.lock.unlock()
                return
            }

            var didAppendData = false
            if let data, !data.isEmpty {
                let remaining = Self.maxResponseBytes - self.state.buffer.count
                if remaining > 0 {
                    self.state.buffer.append(data.prefix(remaining))
                    didAppendData = true
                }
            }

            if isComplete || error != nil || self.state.buffer.count >= Self.maxResponseBytes {
                terminalSnapshot = self.state.buffer
            } else if didAppendData, Self.shouldAttemptIncrementalParse(self.state.buffer) {
                incrementalSnapshot = self.state.buffer
            }
            self.lock.unlock()

            if let snapshot = terminalSnapshot {
                let parsed = CampusQuotaParser.parseHTTPResponse(snapshot) ?? .unreachable
                self.finish(parsed)
                return
            }

            if let snapshot = incrementalSnapshot,
               let parsed = CampusQuotaParser.parseHTTPResponse(snapshot)
            {
                switch parsed {
                case .online, .offline:
                    self.finish(parsed)
                    return
                case .unreachable:
                    break
                }
            }

            self.receiveNextChunk(on: connection)
        }
    }

    private static func shouldAttemptIncrementalParse(_ buffer: Data) -> Bool {
        guard let separatorRange = buffer.range(of: crlfSeparator) ?? buffer.range(of: lfSeparator) else {
            return false
        }
        return buffer[separatorRange.upperBound...].contains(0x7D)
    }

    private func finish(_ result: PortalQueryResult) {
        lock.lock()
        let cont: CheckedContinuation<PortalQueryResult, Never>?
        let conn: NWConnection?
        let timer: DispatchSourceTimer?
        if state.finished {
            cont = nil
            conn = nil
            timer = nil
        } else {
            state.finished = true
            cont = state.continuation
            state.continuation = nil
            conn = state.connection
            state.connection = nil
            timer = state.timer
            state.timer = nil
        }
        lock.unlock()

        timer?.cancel()
        conn?.stateUpdateHandler = nil
        conn?.cancel()
        cont?.resume(returning: result)
    }
}
