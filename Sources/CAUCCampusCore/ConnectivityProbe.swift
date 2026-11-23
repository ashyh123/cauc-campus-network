import Foundation
import Network

public enum IPFamily: Sendable {
    case ipv4
    case ipv6
}

public struct ConnectivityProber: Sendable {
    private let timeout: Duration

    public init(timeout: Duration = .milliseconds(3500)) {
        self.timeout = timeout
    }

    public func check(ustcFallback: Bool) async -> ConnectivityStatus {
        let v6Targets = ustcFallback
            ? CampusEndpoints.publicTargets + [CampusEndpoints.ustcIPv6Probe]
            : CampusEndpoints.publicTargets

        async let v4 = probe(family: .ipv4, targets: CampusEndpoints.publicTargets)
        async let v6 = probe(family: .ipv6, targets: v6Targets)
        return ConnectivityStatus(ipv4: await v4, ipv6: await v6)
    }

    public func probe(family: IPFamily, targets: [URL]) async -> Bool {
        guard !targets.isEmpty else { return false }

        return await withTaskGroup(of: Bool.self) { group in
            for target in targets {
                group.addTask {
                    await probeSingle(family: family, url: target)
                }
            }

            while let result = await group.next() {
                if result {
                    group.cancelAll()
                    return true
                }
            }
            return false
        }
    }

    public func probeSingle(family: IPFamily, url: URL) async -> Bool {
        guard let target = ProbeTarget(url: url) else {
            return false
        }

        let runner = ProbeRunner(family: family, target: target, timeout: timeout)
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                runner.start(continuation: continuation)
            }
        } onCancel: {
            runner.finish(false)
        }
    }
}

private struct ProbeTarget: Sendable {
    let host: String
    let port: UInt16
    let isHTTPS: Bool
    let requestData: Data

    init?(url: URL) {
        guard let rawHost = url.host, !rawHost.isEmpty else {
            return nil
        }

        let scheme = url.scheme?.lowercased()
        let isHTTPS = scheme == "https"
        guard scheme == "http" || isHTTPS else {
            return nil
        }

        let cleanHost = rawHost.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        let defaultPort = isHTTPS ? 443 : 80
        let resolvedPort = url.port ?? defaultPort
        guard resolvedPort > 0, resolvedPort <= Int(UInt16.max) else {
            return nil
        }
        let port = UInt16(resolvedPort)

        let basePath = url.path.isEmpty ? "/" : url.path
        let requestTarget = url.query.map { "\(basePath)?\($0)" } ?? basePath

        let formattedHost = cleanHost.contains(":") ? "[\(cleanHost)]" : cleanHost
        let hostHeader = (resolvedPort == defaultPort) ? formattedHost : "\(formattedHost):\(port)"
        let rawRequest = "GET \(requestTarget) HTTP/1.1\r\nHost: \(hostHeader)\r\nUser-Agent: CAUCCampusNetwork/2.0\r\nConnection: close\r\n\r\n"

        self.host = cleanHost
        self.port = port
        self.isHTTPS = isHTTPS
        self.requestData = Data(rawRequest.utf8)
    }
}

private final class ProbeRunner: @unchecked Sendable {
    private struct State {
        var continuation: CheckedContinuation<Bool, Never>?
        var finished = false
        var buffer = Data()
        var timer: DispatchSourceTimer?
        var connection: NWConnection?
    }

    private let lock = NSLock()
    private var state = State()
    private let family: IPFamily
    private let target: ProbeTarget
    private let timeout: Duration
    private let queue = DispatchQueue(label: "local.cauc.campus-network.probe")

    init(family: IPFamily, target: ProbeTarget, timeout: Duration) {
        self.family = family
        self.target = target
        self.timeout = timeout
    }

    func start(continuation: CheckedContinuation<Bool, Never>) {
        if (family == .ipv4 && IPv6Address(target.host) != nil)
            || (family == .ipv6 && IPv4Address(target.host) != nil)
        {
            continuation.resume(returning: false)
            return
        }

        guard let nwPort = NWEndpoint.Port(rawValue: target.port) else {
            continuation.resume(returning: false)
            return
        }

        let parameters: NWParameters = target.isHTTPS ? .tls : .tcp
        if let ipOptions = parameters.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            ipOptions.version = (family == .ipv4) ? .v4 : .v6
        }

        let connection = NWConnection(
            host: NWEndpoint.Host(target.host),
            port: nwPort,
            using: parameters
        )

        let timer = DispatchSource.makeTimerSource(queue: queue)
        let timeoutNanoseconds = max(1, timeout.components.seconds * 1_000_000_000 + timeout.components.attoseconds / 1_000_000_000)

        lock.lock()
        let shouldStart = !state.finished
        if shouldStart {
            state.continuation = continuation
            state.connection = connection
            state.timer = timer
        }
        lock.unlock()

        guard shouldStart else {
            continuation.resume(returning: false)
            return
        }

        timer.schedule(deadline: .now() + .nanoseconds(Int(clamping: timeoutNanoseconds)))
        timer.setEventHandler { [weak self] in
            self?.finish(false)
        }
        timer.resume()

        connection.stateUpdateHandler = { [weak self] newState in
            self?.handleConnectionState(newState, on: connection)
        }
        connection.start(queue: queue)
    }

    private func handleConnectionState(_ state: NWConnection.State, on connection: NWConnection) {
        switch state {
        case .ready:
            guard matchesFamily(connection.currentPath?.remoteEndpoint),
                  matchesFamily(connection.currentPath?.localEndpoint)
            else {
                finish(false)
                return
            }
            connection.send(content: target.requestData, completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                if error != nil {
                    self.finish(false)
                    return
                }
                self.receiveNextChunk(on: connection)
            })
        case .waiting, .failed, .cancelled:
            finish(false)
        case .setup, .preparing:
            break
        @unknown default:
            break
        }
    }

    private func matchesFamily(_ endpoint: NWEndpoint?) -> Bool {
        guard case .hostPort(let host, _) = endpoint else {
            return true
        }
        switch host {
        case .ipv4:
            return family == .ipv4
        case .ipv6(let addr):
            return family == .ipv6 && addr.asIPv4 == nil
        case .name:
            return true
        @unknown default:
            return true
        }
    }

    private func receiveNextChunk(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, isComplete, error in
            guard let self else { return }

            if let data, !data.isEmpty {
                self.lock.lock()
                let parsedStatus: Int??
                let bufferTooLarge: Bool
                if self.state.finished {
                    parsedStatus = nil
                    bufferTooLarge = false
                } else {
                    self.state.buffer.append(data)
                    parsedStatus = Self.parseStatusCode(from: self.state.buffer, isEOF: isComplete)
                    bufferTooLarge = self.state.buffer.count >= 4096
                }
                self.lock.unlock()

                switch parsedStatus {
                case .none:
                    return
                case .some(.some(let code)):
                    self.finish((200..<300).contains(code))
                    return
                case .some(.none):
                    if bufferTooLarge || isComplete || error != nil {
                        self.finish(false)
                        return
                    }
                    self.receiveNextChunk(on: connection)
                    return
                }
            }

            if isComplete || error != nil {
                self.lock.lock()
                let finalCode = self.state.finished ? nil : Self.parseStatusCode(from: self.state.buffer, isEOF: true)
                self.lock.unlock()
                self.finish(finalCode.map { (200..<300).contains($0) } ?? false)
            } else {
                self.receiveNextChunk(on: connection)
            }
        }
    }

    func finish(_ result: Bool) {
        lock.lock()
        let cont: CheckedContinuation<Bool, Never>?
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

    private static func parseStatusCode(from data: Data, isEOF: Bool) -> Int? {
        let newline = Data([0x0A])
        let lineData: Data
        if let newlineRange = data.range(of: newline) {
            lineData = data[..<newlineRange.lowerBound]
        } else if isEOF && !data.isEmpty {
            lineData = data
        } else {
            return nil
        }

        guard let line = String(data: lineData, encoding: .utf8)?
            .trimmingCharacters(in: CharacterSet.whitespacesAndNewlines),
              line.hasPrefix("HTTP/")
        else {
            return -1
        }

        let parts = line.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
        guard parts.count >= 2, let code = Int(parts[1]) else {
            return -1
        }
        return code
    }
}
