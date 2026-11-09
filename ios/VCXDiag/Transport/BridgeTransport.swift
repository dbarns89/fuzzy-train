import Foundation
import Network

/// Talks to `bridge/vcx_bridge.py` on a Windows PC that has the VCX Nano attached.
/// Protocol: one JSON object per line over TCP.
final class BridgeTransport: DiagTransport {
    let host: String
    let port: UInt16
    var name: String { "VCX bridge \(host):\(port)" }

    private var connection: NWConnection?
    private let queue = DispatchQueue(label: "bridge.transport")
    private var nextId = 1
    private var buffer = Data()
    private var pending: [Int: CheckedContinuation<[String: Any], Error>] = [:]
    private let lock = NSLock()

    init(host: String, port: UInt16 = 7534) {
        self.host = host
        self.port = port
    }

    func connect() async throws {
        let conn = NWConnection(host: NWEndpoint.Host(host),
                                port: NWEndpoint.Port(rawValue: port)!,
                                using: .tcp)
        connection = conn
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            var resumed = false
            conn.stateUpdateHandler = { state in
                guard !resumed else { return }
                switch state {
                case .ready: resumed = true; cont.resume()
                case .failed(let err): resumed = true; cont.resume(throwing: err)
                case .waiting(let err): resumed = true; conn.cancel(); cont.resume(throwing: err)
                default: break
                }
            }
            conn.start(queue: queue)
        }
        receiveLoop()
        _ = try await call(["op": "hello"])
        _ = try await call(["op": "open"])
    }

    func disconnect() async {
        _ = try? await call(["op": "close"])
        connection?.cancel()
        connection = nil
        failAll(TransportError.notConnected)
    }

    func adapterInfo() async throws -> AdapterInfo {
        let r = try await call(["op": "info"])
        return AdapterInfo(firmware: r["firmware"] as? String ?? "?",
                           driver: r["dll"] as? String ?? "?",
                           batteryVolts: r["battery_v"] as? Double ?? 0)
    }

    func openBus(_ proto: BusProtocol, baud: Int) async throws -> Int {
        let r = try await call(["op": "connect", "protocol": proto.rawValue, "baud": baud])
        guard let ch = r["channel"] as? Int else { throw TransportError.badResponse }
        return ch
    }

    func closeBus(_ channel: Int) async throws {
        _ = try await call(["op": "disconnect", "channel": channel])
    }

    func setFilter(channel: Int, mask: Data, pattern: Data, flowControl: Data?) async throws {
        var msg: [String: Any] = ["op": "filter", "channel": channel,
                                  "mask": mask.hex, "pattern": pattern.hex, "pad": true]
        if let fc = flowControl { msg["flow"] = fc.hex }
        _ = try await call(msg)
    }

    func request(channel: Int, _ data: Data, timeoutMs: Int) async throws -> [Data] {
        let r = try await call(["op": "request", "channel": channel, "data": data.hex,
                                "timeout_ms": timeoutMs, "pad": true],
                               waitSeconds: Double(timeoutMs) / 1000 + 5)
        let frames = r["frames"] as? [String] ?? []
        return frames.compactMap { Data(hex: $0) }
    }

    // MARK: - JSON line RPC

    private func call(_ body: [String: Any], waitSeconds: Double = 5) async throws -> [String: Any] {
        guard let conn = connection else { throw TransportError.notConnected }
        lock.lock(); let id = nextId; nextId += 1; lock.unlock()
        var msg = body; msg["id"] = id
        var line = try JSONSerialization.data(withJSONObject: msg)
        line.append(0x0A)

        let response: [String: Any] = try await withCheckedThrowingContinuation { cont in
            lock.lock(); pending[id] = cont; lock.unlock()
            conn.send(content: line, completion: .contentProcessed { [weak self] err in
                if let err { self?.resolve(id, .failure(err)) }
            })
            queue.asyncAfter(deadline: .now() + waitSeconds) { [weak self] in
                self?.resolve(id, .failure(TransportError.timeout))
            }
        }
        if response["ok"] as? Bool == true { return response }
        throw TransportError.bridge(response["error"] as? String ?? "Bridge error")
    }

    private func resolve(_ id: Int, _ result: Result<[String: Any], Error>) {
        lock.lock(); let cont = pending.removeValue(forKey: id); lock.unlock()
        cont?.resume(with: result)
    }

    private func failAll(_ error: Error) {
        lock.lock(); let all = pending; pending.removeAll(); lock.unlock()
        all.values.forEach { $0.resume(throwing: error) }
    }

    private func receiveLoop() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, done, err in
            guard let self else { return }
            if let data { self.buffer.append(data); self.drainLines() }
            if done || err != nil {
                self.failAll(err ?? TransportError.notConnected)
                return
            }
            self.receiveLoop()
        }
    }

    private func drainLines() {
        while let nl = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<nl]
            buffer.removeSubrange(buffer.startIndex...nl)
            guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let id = obj["id"] as? Int else { continue }
            resolve(id, .success(obj))
        }
    }
}

extension Data {
    var hex: String { map { String(format: "%02X", $0) }.joined() }

    init?(hex: String) {
        let chars = Array(hex.filter { !$0.isWhitespace })
        guard chars.count % 2 == 0 else { return nil }
        var out = Data(capacity: chars.count / 2)
        for i in stride(from: 0, to: chars.count, by: 2) {
            guard let b = UInt8(String(chars[i...i + 1]), radix: 16) else { return nil }
            out.append(b)
        }
        self = out
    }
}
