import Foundation

/// Vehicle bus protocols the app can open. Raw values match the bridge's names.
enum BusProtocol: String, CaseIterable, Identifiable {
    case j1850vpw = "J1850VPW"        // GM Class 2, ~1996-2007
    case iso15765 = "ISO15765"        // HS-CAN / GMLAN high speed with ISO-TP
    case swCanIsoTp = "SW_ISO15765_PS" // GMLAN single-wire CAN (body modules)

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .j1850vpw: return "J1850 VPW (Class 2)"
        case .iso15765: return "HS-CAN / GMLAN"
        case .swCanIsoTp: return "GMLAN single-wire"
        }
    }

    var defaultBaud: Int {
        switch self {
        case .j1850vpw: return 10_400
        case .iso15765: return 500_000
        case .swCanIsoTp: return 33_333
        }
    }
}

struct AdapterInfo: Equatable {
    var firmware: String
    var driver: String
    var batteryVolts: Double
}

enum TransportError: LocalizedError {
    case notConnected
    case bridge(String)
    case timeout
    case badResponse

    var errorDescription: String? {
        switch self {
        case .notConnected: return "Not connected to an adapter."
        case .bridge(let msg): return msg
        case .timeout: return "The adapter didn't answer in time."
        case .badResponse: return "The adapter sent a response the app couldn't read."
        }
    }
}

/// Anything that can put bytes on a vehicle bus: the PC bridge for the VCX Nano
/// today, and a Bluetooth adapter (e.g. OBDLink MX+) later.
protocol DiagTransport: AnyObject {
    var name: String { get }
    func connect() async throws
    func disconnect() async
    func adapterInfo() async throws -> AdapterInfo
    /// Opens a bus channel and returns a handle for later calls.
    func openBus(_ proto: BusProtocol, baud: Int) async throws -> Int
    func closeBus(_ channel: Int) async throws
    /// Lets frames whose leading bytes match `pattern` under `mask` through. On ISO-TP
    /// channels `flowControl` is the CAN ID the adapter should send flow control to.
    func setFilter(channel: Int, mask: Data, pattern: Data, flowControl: Data?) async throws
    /// Sends one message and collects every response frame until `timeoutMs` passes.
    func request(channel: Int, _ data: Data, timeoutMs: Int) async throws -> [Data]
}
