import XCTest
@testable import VCXDiag

final class DecodeTests: XCTestCase {
    func testDTCDecode() {
        XCTAssertEqual(DTC.decode(0x01, 0x71, module: "ECM")?.code, "P0171")
        XCTAssertEqual(DTC.decode(0x04, 0x20, module: "ECM")?.code, "P0420")
        XCTAssertEqual(DTC.decode(0x40, 0x35, module: "ABS")?.code, "C0035")
        XCTAssertEqual(DTC.decode(0xD0, 0x73, module: "BCM")?.code, "U1073")
        XCTAssertNil(DTC.decode(0x00, 0x00, module: "ECM"))
    }

    func testVPWParse() {
        let r = VPWDialect().parse(Data(hex: "486B10410C0BB8")!)
        XCTAssertEqual(r?.source, 0x10)
        XCTAssertEqual(r?.payload, [0x41, 0x0C, 0x0B, 0xB8])
        let rpm = PID.common.first { $0.id == 0x0C }!.decode([0x0B, 0xB8])
        XCTAssertEqual(rpm, 750)
    }

    func testCANParse() {
        let r = CANDialect(proto: .iso15765).parse(Data(hex: "000007E8410D32")!)
        XCTAssertEqual(r?.source, 0x7E0)
        XCTAssertEqual(r?.payload, [0x41, 0x0D, 0x32])
    }
}
