import Foundation

/// A diagnostic trouble code such as P0420.
struct DTC: Identifiable, Hashable {
    let code: String
    let module: String
    var status: String?
    var id: String { "\(module)-\(code)" }

    var description: String { DTCDescriptions.text(for: code) ?? "No description in the built-in list" }

    /// Decodes the standard two-byte SAE J2012 form.
    static func decode(_ hi: UInt8, _ lo: UInt8, module: String) -> DTC? {
        if hi == 0 && lo == 0 { return nil }
        let system = ["P", "C", "B", "U"][Int(hi >> 6)]
        let code = system + String(hi >> 4 & 0x3) + String(format: "%01X%02X", hi & 0x0F, lo)
        return DTC(code: code, module: module)
    }
}

/// Small built-in set of generic SAE descriptions. Add more over time; GM-specific
/// meanings vary by model year and module, so the app never guesses them.
enum DTCDescriptions {
    static func text(for code: String) -> String? { table[code] }
    private static let table: [String: String] = [
        "P0101": "Mass air flow sensor range/performance",
        "P0128": "Coolant temperature below thermostat regulating temperature",
        "P0171": "System too lean (bank 1)",
        "P0174": "System too lean (bank 2)",
        "P0300": "Random/multiple cylinder misfire detected",
        "P0420": "Catalyst efficiency below threshold (bank 1)",
        "P0442": "EVAP system small leak detected",
        "P0455": "EVAP system large leak detected",
        "P0700": "Transmission control system malfunction",
        "U0100": "Lost communication with ECM/PCM",
    ]
}

/// Generic OBD-II Mode $01 parameters, which every 1996+ GM vehicle supports.
struct PID: Identifiable, Hashable {
    let id: UInt8
    let name: String
    let unit: String
    let bytes: Int
    let decode: ([UInt8]) -> Double

    static func == (a: PID, b: PID) -> Bool { a.id == b.id }
    func hash(into h: inout Hasher) { h.combine(id) }

    static let common: [PID] = [
        PID(id: 0x04, name: "Engine load", unit: "%", bytes: 1) { Double($0[0]) * 100 / 255 },
        PID(id: 0x05, name: "Coolant temp", unit: "°C", bytes: 1) { Double($0[0]) - 40 },
        PID(id: 0x06, name: "Short term fuel trim B1", unit: "%", bytes: 1) { (Double($0[0]) - 128) * 100 / 128 },
        PID(id: 0x07, name: "Long term fuel trim B1", unit: "%", bytes: 1) { (Double($0[0]) - 128) * 100 / 128 },
        PID(id: 0x0B, name: "Intake manifold pressure", unit: "kPa", bytes: 1) { Double($0[0]) },
        PID(id: 0x0C, name: "Engine RPM", unit: "rpm", bytes: 2) { (Double($0[0]) * 256 + Double($0[1])) / 4 },
        PID(id: 0x0D, name: "Vehicle speed", unit: "km/h", bytes: 1) { Double($0[0]) },
        PID(id: 0x0E, name: "Timing advance", unit: "°", bytes: 1) { Double($0[0]) / 2 - 64 },
        PID(id: 0x0F, name: "Intake air temp", unit: "°C", bytes: 1) { Double($0[0]) - 40 },
        PID(id: 0x10, name: "MAF air flow", unit: "g/s", bytes: 2) { (Double($0[0]) * 256 + Double($0[1])) / 100 },
        PID(id: 0x11, name: "Throttle position", unit: "%", bytes: 1) { Double($0[0]) * 100 / 255 },
    ]
}
