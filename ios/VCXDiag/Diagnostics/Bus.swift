import Foundation

/// A GM module on the bus (ECM, TCM, BCM...).
struct Module: Identifiable, Hashable {
    let address: UInt32   // VPW node address, or CAN request ID
    let name: String
    var id: UInt32 { address }
}

/// Builds request frames and parses replies for one bus type, so the rest of the
/// app can ask for "DTCs from the ECM" without knowing VPW headers or CAN IDs.
protocol BusDialect {
    var proto: BusProtocol { get }
    var knownModules: [Module] { get }
    /// Filter set up right after the bus opens.
    func configure(transport: DiagTransport, channel: Int) async throws
    /// Generic OBD request to the engine controller (mode + payload).
    func obdRequest(_ payload: [UInt8]) -> Data
    /// Strips headers and returns (source module address, service payload).
    func parse(_ frame: Data) -> (source: UInt32, payload: [UInt8])?
}

/// GM Class 2 / SAE J1850 VPW.
/// Header: priority/type byte, target, source. The scan tool is 0xF1.
struct VPWDialect: BusDialect {
    let proto = BusProtocol.j1850vpw
    static let tester: UInt8 = 0xF1

    // Common GM Class 2 node addresses (SAE J2178). Not every vehicle has all of them.
    let knownModules = [
        Module(address: 0x10, name: "Engine (ECM/PCM)"),
        Module(address: 0x18, name: "Transmission (TCM)"),
        Module(address: 0x28, name: "Brakes / ABS (EBCM)"),
        Module(address: 0x40, name: "Body (BCM)"),
        Module(address: 0x58, name: "Airbag (SDM)"),
        Module(address: 0x60, name: "Instrument cluster (IPC)"),
        Module(address: 0x80, name: "Radio"),
        Module(address: 0x99, name: "HVAC"),
    ]

    func configure(transport: DiagTransport, channel: Int) async throws {
        // Accept every frame addressed to the tester: byte 1 (target) == F1, or the
        // functional OBD response target 0x6B.
        try await transport.setFilter(channel: channel,
                                      mask: Data([0x00, 0x00, 0x00]),
                                      pattern: Data([0x00, 0x00, 0x00]),
                                      flowControl: nil)
    }

    /// Functional OBD-II request: 68 6A F1 <mode> <pid...>
    func obdRequest(_ payload: [UInt8]) -> Data {
        Data([0x68, 0x6A, Self.tester] + payload)
    }

    /// Physical GM-enhanced request to one module: 6C <target> F1 <service...>
    func physical(_ target: UInt8, _ payload: [UInt8]) -> Data {
        Data([0x6C, target, Self.tester] + payload)
    }

    func parse(_ frame: Data) -> (source: UInt32, payload: [UInt8])? {
        let b = [UInt8](frame)
        guard b.count >= 4, b[1] == Self.tester || b[1] == 0x6B else { return nil }
        // Some adapters leave the trailing CRC on; J2534 normally strips it.
        return (UInt32(b[2]), Array(b[3...]))
    }
}

/// ISO 15765-4 (HS-CAN, 11-bit IDs) as used by GMLAN and OBD-II on 2008+ GM.
struct CANDialect: BusDialect {
    let proto: BusProtocol

    // OBD-II CAN response IDs are request + 8; GM uses the same scheme for its
    // main powertrain modules.
    let knownModules = [
        Module(address: 0x7E0, name: "Engine (ECM)"),
        Module(address: 0x7E2, name: "Transmission (TCM)"),
        Module(address: 0x7E1, name: "Hybrid / transmission 2"),
        Module(address: 0x243, name: "Brakes (EBCM)"),
        Module(address: 0x241, name: "Body (BCM)"),
        Module(address: 0x24C, name: "Instrument cluster (IPC)"),
    ]

    func configure(transport: DiagTransport, channel: Int) async throws {
        // One ISO-TP flow control filter per module we may talk to.
        for m in knownModules {
            let rx = m.address >= 0x7E0 ? m.address + 8 : m.address + 0x400
            try await transport.setFilter(channel: channel,
                                          mask: Self.id(0x7FF),
                                          pattern: Self.id(rx),
                                          flowControl: Self.id(m.address))
        }
    }

    func obdRequest(_ payload: [UInt8]) -> Data { request(to: 0x7E0, payload) }

    func request(to id: UInt32, _ payload: [UInt8]) -> Data { Self.id(id) + Data(payload) }

    func parse(_ frame: Data) -> (source: UInt32, payload: [UInt8])? {
        let b = [UInt8](frame)
        guard b.count >= 5 else { return nil }
        let rx = UInt32(b[0]) << 24 | UInt32(b[1]) << 16 | UInt32(b[2]) << 8 | UInt32(b[3])
        let src = rx >= 0x7E8 ? rx - 8 : rx - 0x400
        return (src, Array(b[4...]))
    }

    static func id(_ v: UInt32) -> Data {
        Data([UInt8(v >> 24 & 0xFF), UInt8(v >> 16 & 0xFF), UInt8(v >> 8 & 0xFF), UInt8(v & 0xFF)])
    }
}
