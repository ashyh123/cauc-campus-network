import Foundation
import Network
import Testing
import os
@testable import CAUCCampusCore

@Suite("ConnectivityProber Tests")
struct ConnectivityProbeTests {
    @Test("Uses requested IP family and rejects HTTP redirects")
    func probesRequestedFamilyAndRejectsRedirects() async throws {
        let prober = ConnectivityProber(timeout: .milliseconds(800))

        let v4Server = try await LocalHTTPProbeServer.start(family: .ipv4)
        defer { v4Server?.stop() }

        if let v4Server {
            let v4Base = "http://127.0.0.1:\(v4Server.port)"
            #expect(await prober.probeSingle(family: .ipv4, url: URL(string: "\(v4Base)/ok")!) == true)
            #expect(await prober.probeSingle(family: .ipv6, url: URL(string: "\(v4Base)/ok")!) == false)
            #expect(await prober.probeSingle(family: .ipv4, url: URL(string: "\(v4Base)/portal")!) == false)
        }

        let v6Server = try await LocalHTTPProbeServer.start(family: .ipv6)
        defer { v6Server?.stop() }

        if let v6Server {
            let v6Base = "http://[::1]:\(v6Server.port)"
            #expect(await prober.probeSingle(family: .ipv6, url: URL(string: "\(v6Base)/ok")!) == true)
            #expect(await prober.probeSingle(family: .ipv4, url: URL(string: "\(v6Base)/ok")!) == false)
            #expect(await prober.probeSingle(family: .ipv6, url: URL(string: "\(v6Base)/portal")!) == false)
        }

        #expect(v4Server != nil || v6Server != nil)
    }
}

private final class LocalHTTPProbeServer: @unchecked Sendable {
    let port: UInt16
    private let listener: NWListener
    private let queue = DispatchQueue(label: "local.cauc.campus-network.test-server")

    private init(listener: NWListener, port: UInt16) {
        self.listener = listener
        self.port = port
    }

    static func start(family: IPFamily) async throws -> LocalHTTPProbeServer? {
        let parameters = NWParameters.tcp
        if let ipOptions = parameters.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            ipOptions.version = (family == .ipv4) ? .v4 : .v6
        }
        let host = NWEndpoint.Host(family == .ipv4 ? "127.0.0.1" : "::1")
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: host, port: .any)

        let listener = try NWListener(using: parameters)
        let queue = DispatchQueue(label: "local.cauc.campus-network.test-listener")
        let gate = OSAllocatedUnfairLock(initialState: false)

        let boundPort: UInt16? = await withCheckedContinuation { continuation in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    let alreadyResumed = gate.withLock { done -> Bool in
                        if done { return true }
                        done = true
                        return false
                    }
                    if !alreadyResumed {
                        continuation.resume(returning: listener.port?.rawValue)
                    }
                case .failed, .cancelled:
                    let alreadyResumed = gate.withLock { done -> Bool in
                        if done { return true }
                        done = true
                        return false
                    }
                    if !alreadyResumed {
                        continuation.resume(returning: nil)
                    }
                default:
                    break
                }
            }

            listener.newConnectionHandler = { connection in
                Self.handle(connection: connection, queue: queue)
            }
            listener.start(queue: queue)
        }

        guard let boundPort else {
            listener.cancel()
            return nil
        }

        return LocalHTTPProbeServer(listener: listener, port: boundPort)
    }

    func stop() {
        listener.cancel()
    }

    private static func handle(connection: NWConnection, queue: DispatchQueue) {
        connection.stateUpdateHandler = { state in
            if case .ready = state {
                connection.receive(minimumIncompleteLength: 1, maximumLength: 2048) { data, _, _, _ in
                    let requestText = data.flatMap { String(data: $0, encoding: .utf8) } ?? ""
                    let firstLine = requestText.split(separator: "\r\n", maxSplits: 1).first ?? ""
                    let response: String
                    if firstLine.contains(" /portal ") {
                        response = "HTTP/1.1 302 Found\r\nLocation: /ok\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    } else {
                        response = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                    }
                    connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in
                        connection.cancel()
                    })
                }
            }
        }
        connection.start(queue: queue)
    }
}
