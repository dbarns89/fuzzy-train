# VCX Diag

An iPhone app for GM diagnostics with a VXDIAG VCX Nano, covering the everyday
Tech2-style jobs: find modules, read and clear trouble codes, read the VIN, and
watch live data. Supports GM Class 2 (J1850 VPW) and HS-CAN / GMLAN.

This is original code. It does not include or run GM's Tech2 / Tech2Win software.

## How it connects

An iPhone can't talk to the VCX Nano directly (iOS blocks generic USB devices, and
the WiFi model's protocol is undocumented). So the VCX Nano stays plugged into a
Windows PC, and a small bridge program exposes it to the app over your WiFi:

    iPhone app  --WiFi/TCP 7534-->  vcx_bridge.py (Windows)  --J2534-->  VCX Nano  -->  car

The app's `DiagTransport` protocol keeps the connection swappable, so a Bluetooth
adapter such as the OBDLink MX+ can be added later as a second option.

## 1. Run the bridge (Windows PC)

1. Install VX Manager and the GM driver for the VCX Nano (the same setup Tech2Win uses).
2. Install **32-bit** Python 3.9 or newer (the VCX J2534 DLL is 32-bit).
3. `python bridge\vcx_bridge.py --list` to see the J2534 driver, then
   `python bridge\vcx_bridge.py` to start. It prints the address to type into the app.
4. Allow it through Windows Firewall on private networks.

No car handy? `python bridge/vcx_bridge.py --simulate` fakes a 2004 GM truck on
Class 2, with codes in the ECM, ABS and BCM. Works on any OS.

## 2. Build the app (Mac with Xcode 15+)

    brew install xcodegen
    cd ios && xcodegen && open VCXDiag.xcodeproj

Pick your team under Signing, plug in your iPhone, and run. iOS will ask for local
network permission on first connect.

## Status

| Feature | Class 2 (VPW) | HS-CAN / GMLAN |
|---|---|---|
| Module scan | yes ($19 to each known address) | powertrain only (tester present) |
| Read codes, all modules | yes (GM $19) | engine via OBD mode $03 |
| Clear codes | yes ($14 + mode $04) | engine via mode $04 |
| VIN | yes | yes |
| Live data | generic OBD PIDs | generic OBD PIDs |

Next: GMLAN enhanced codes ($A9), GM-specific data parameters per module, single-wire
CAN body modules, Bluetooth (OBDLink MX+) transport, and testing on a real vehicle.
Module addresses and GM request formats come from public SAE/GM documentation and
must be confirmed on a real vehicle before trusting them.
