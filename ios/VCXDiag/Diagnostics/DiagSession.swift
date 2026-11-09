import Foundation
import SwiftUI

/// App-wide diagnostic state: the adapter connection, the open bus, and results.
@MainActor
final class DiagSession: ObservableObject {
    enum Phase: Equatable { case disconnected, connecting, connected, busOpen }

    @Published var phase: Phase = .disconnected
    @Published var adapter: AdapterInfo?
    @Published var bus: BusProtocol = .j1850vpw
    @Published var modules: [Module] = []
    @Published var dtcs: [DTC] = []
    @Published var vin: String?
    @Published var liveValues: [UInt8: Double] = [:]
    @Published var log: [String] = []
    @Published var error: String?
    @Published var busy = false

    @Published var bridgeHost = UserDefaults.standard.string(forKey: "bridgeHost") ?? "192.168.1.50" {
        didSet { UserDefaults.standard.set(bridgeHost, forKey: "bridgeHost") }
    }

    private var transport: DiagTransport?
    private var channel: Int?
    private var dialect: BusDialect = VPWDialect()
    private var liveTask: Task<Void, Never>?

    // MARK: Connection

    func connectBridge() async {
        await run("Connect") {
            self.phase = .connecting
            let t = BridgeTransport(host: self.bridgeHost)
            try await t.connect()
            self.transport = t
            self.adapter = try? await t.adapterInfo()
            self.phase = .connected
        }
        if phase == .connecting { phase = .disconnected }
    }

    func disconnect() async {
        stopLive()
        if let t = transport, let ch = channel { try? await t.closeBus(ch) }
        await transport?.disconnect()
        transport = nil; channel = nil
        modules = []; dtcs = []; vin = nil; adapter = nil
        phase = .disconnected
    }

    func openBus(_ proto: BusProtocol) async {
        await run("Open \(proto.displayName)") {
            guard let t = self.transport else { throw TransportError.notConnected }
            if let ch = self.channel { try? await t.closeBus(ch) }
            let ch = try await t.openBus(proto, baud: proto.defaultBaud)
            let d: BusDialect = proto == .j1850vpw ? VPWDialect() : CANDialect(proto: proto)
            try await d.configure(transport: t, channel: ch)
            self.channel = ch; self.dialect = d; self.bus = proto
            self.phase = .busOpen
        }
    }

    // MARK: Vehicle functions

    /// Reads codes from every known module. Any module that answers (with codes,
    /// no codes, or a refusal) is listed as present.
    func scanAllModules() async {
        await run("Scan modules") {
            var found: [Module] = []
            var codes: [DTC] = []
            for m in self.dialect.knownModules {
                let (present, list) = try await self.readEnhancedDTCs(m)
                if present { found.append(m) }
                codes += list
            }
            // Emissions codes via generic OBD-II mode $03, merged without duplicates.
            for c in try await self.readGenericDTCs() where !codes.contains(where: { $0.code == c.code }) {
                codes.append(c)
            }
            self.modules = found
            self.dtcs = codes
        }
    }

    func clearAllDTCs() async {
        await run("Clear codes") {
            let (t, ch) = try self.channelPair()
            // Generic OBD-II clear (mode $04) for the engine controller.
            _ = try await t.request(channel: ch, self.dialect.obdRequest([0x04]), timeoutMs: 500)
            // GM enhanced clear (service $14) for each module that answered the scan.
            if let vpw = self.dialect as? VPWDialect {
                for m in self.modules {
                    _ = try await t.request(channel: ch, vpw.physical(UInt8(m.address), [0x14]), timeoutMs: 300)
                }
            }
            self.dtcs = []
        }
    }

    func readVIN() async {
        await run("Read VIN") {
            let (t, ch) = try self.channelPair()
            let frames = try await t.request(channel: ch, self.dialect.obdRequest([0x09, 0x02]), timeoutMs: 800)
            var bytes: [UInt8] = []
            if self.bus == .j1850vpw {
                // VPW: five frames of 49 02 <seq> b1 b2 b3 b4; first has 3 pad bytes.
                let parts = frames.compactMap { self.dialect.parse($0)?.payload }
                    .filter { $0.count >= 7 && $0[0] == 0x49 && $0[1] == 0x02 }
                    .sorted { $0[2] < $1[2] }
                bytes = parts.flatMap { $0[3...] }
            } else if let p = frames.compactMap({ self.dialect.parse($0)?.payload }).first,
                      p.count > 3, p[0] == 0x49 {
                bytes = Array(p[3...])  // CAN: 49 02 01 + 17 bytes (ISO-TP reassembled)
            }
            let text = String(bytes: bytes.filter { $0 >= 0x30 && $0 <= 0x5A }, encoding: .ascii) ?? ""
            self.vin = text.isEmpty ? nil : text
        }
    }

    func startLive(_ pids: [PID]) {
        stopLive()
        liveTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                for pid in pids {
                    if Task.isCancelled { return }
                    if let v = try? await self.readPID(pid) { self.liveValues[pid.id] = v }
                }
                try? await Task.sleep(nanoseconds: 150_000_000)
            }
        }
    }

    func stopLive() { liveTask?.cancel(); liveTask = nil }

    // MARK: Helpers

    private func readPID(_ pid: PID) async throws -> Double? {
        let (t, ch) = try channelPair()
        let frames = try await t.request(channel: ch, dialect.obdRequest([0x01, pid.id]), timeoutMs: 200)
        for f in frames {
            guard let p = dialect.parse(f)?.payload, p.count >= 2 + pid.bytes,
                  p[0] == 0x41, p[1] == pid.id else { continue }
            return pid.decode(Array(p[2..<(2 + pid.bytes)]))
        }
        return nil
    }

    private func readGenericDTCs() async throws -> [DTC] {
        let (t, ch) = try channelPair()
        let frames = try await t.request(channel: ch, dialect.obdRequest([0x03]), timeoutMs: 600)
        var out: [DTC] = []
        for f in frames {
            guard let r = dialect.parse(f), r.payload.first == 0x43 else { continue }
            let (src, p) = r
            let name = dialect.knownModules.first { $0.address == src }?.name ?? "Engine (OBD)"
            // VPW: 43 then pairs. CAN: 43 <count> then pairs.
            let body = bus == .j1850vpw ? Array(p.dropFirst()) : Array(p.dropFirst(2))
            for i in stride(from: 0, to: body.count - 1, by: 2) {
                if let d = DTC.decode(body[i], body[i + 1], module: name) { out.append(d) }
            }
        }
        return out
    }

    /// GM Class 2 "read DTCs by status" (service $19). The module answers with one
    /// 59 hi lo status frame per code, then 59 00 00 xx to end the list.
    /// On CAN this currently falls back to generic OBD for powertrain modules only;
    /// GMLAN enhanced DTC service ($A9) is the next step.
    private func readEnhancedDTCs(_ m: Module) async throws -> (Bool, [DTC]) {
        let (t, ch) = try channelPair()
        guard let vpw = dialect as? VPWDialect else {
            let frames = try await t.request(channel: ch,
                                             (dialect as! CANDialect).request(to: m.address, [0x3E, 0x00]),
                                             timeoutMs: 150)
            return (!frames.isEmpty, [])
        }
        let frames = try await t.request(channel: ch,
                                         vpw.physical(UInt8(m.address), [0x19, 0xD3, 0xFF, 0x00]),
                                         timeoutMs: 400)
        var present = false
        var out: [DTC] = []
        for f in frames {
            guard let r = vpw.parse(f), r.source == m.address else { continue }
            let p = r.payload
            present = true
            if p.count >= 4, p[0] == 0x59, var d = DTC.decode(p[1], p[2], module: m.name) {
                d.status = Self.statusText(p[3])
                out.append(d)
            }
        }
        return (present, out)
    }

    private static func statusText(_ s: UInt8) -> String {
        var parts: [String] = []
        if s & 0x80 != 0 { parts.append("warning lamp on") }
        if s & 0x01 != 0 { parts.append("current") } else { parts.append("history") }
        return parts.joined(separator: ", ")
    }

    private func channelPair() throws -> (DiagTransport, Int) {
        guard let t = transport, let ch = channel else { throw TransportError.notConnected }
        return (t, ch)
    }

    private func run(_ label: String, _ work: @escaping () async throws -> Void) async {
        busy = true; error = nil
        defer { busy = false }
        do {
            try await work()
            log.append("✓ \(label)")
        } catch {
            self.error = error.localizedDescription
            log.append("✗ \(label): \(error.localizedDescription)")
        }
    }
}
