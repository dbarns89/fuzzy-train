# VCX Diag for Windows

A Windows desktop app for GM diagnostics with a VXDIAG VCX Nano (or any other J2534
pass-thru interface). It covers the everyday scan-tool jobs: find modules, read and
clear trouble codes, identify modules, watch and record live data, and check emissions
status. It talks to the interface directly through its J2534 driver, so no bridge or
second program is needed.

This is original software built on public SAE / ISO standards. It is not Tech2Win, does
not contain GM software or data, and is not affiliated with General Motors.

## Quick start

1. Install **VX Manager** and its GM / J2534 (PassThru) driver, the same setup Tech2Win
   and GDS2 use. Plug in the VCX Nano and check VX Manager shows it connected.
2. Run `VcxDiag.exe` (or install with `VcxDiag-Setup.exe`). Nothing else to install;
   the .NET runtime is bundled. Windows SmartScreen may warn because the file is not
   code-signed: choose **More info > Run anyway**.
3. On the **Connect** page pick the VCX driver, leave the network on **Automatic**,
   turn the ignition ON and press **Connect**.
4. Press **Run health check** on the Vehicle page.

No car handy? Pick **Demo vehicle (no hardware)** in the interface list. Every page
works against a built-in simulated truck.

Use the **x86** build unless your J2534 driver is 64-bit (the Connect page tells you if
they don't match). Close Tech2Win, GDS2 and other diagnostic programs first; only one
program can use the interface at a time.

## What it does

| Page | Functions |
|---|---|
| Connect | Finds installed J2534 drivers, automatic network detection, battery voltage, firmware/driver versions, demo mode |
| Vehicle | VIN (with check digit, make and model year), check-engine status, calibration IDs and CVNs, one-click health check of every module |
| Modules | Module scan, identification data (VIN, part numbers, programming date...), per-module read / clear codes |
| Trouble codes | All modules at once, stored / pending / permanent emissions codes, status and failure type, clear all, copy, CSV export |
| Live data | About 90 standard values, supported-only filter, min/max, graph of up to 6 values, CSV recording, your own `$22` parameters |
| Emissions | Freeze frame, readiness monitors, on-board test results (mode $06) with pass/fail |
| Functions | Return-to-normal, tester present, your own saved special functions with a safety confirmation, expert terminal for any request |
| Bus monitor | Live view of every frame on the network, filter, save to file |
| Report & log | Printable HTML vehicle report, full session log |

Networks: GM Class 2 (J1850 VPW), GMLAN high speed CAN, GMLAN single wire CAN, and mid
speed CAN on pins 3/11 (the last two need an interface that supports them).

Files are kept in `Documents\VCX Diag`: `Logs`, `Recordings`, `Reports`, plus three
files you can edit:

- `dtc-descriptions.csv`: your own code descriptions, one `code,description` per line.
- `custom-parameters.json`: extra live data values read with service `$22`.
- `special-functions.json`: saved requests (output controls, resets) shown on the
  Functions page. Each has a name, target (`OBD`, `ALL`, or a module address in hex),
  request bytes, optional stop request, and whether to ask for confirmation.

## What it does not do

- **Built-in output controls, relearns and module programming.** Those procedures and
  the data behind them are GM's proprietary service information, so none ship with the
  app. You can add requests you have documentation for as special functions.
- **Security access and programming (SPS).** Not supported.
- **GM-specific live data lists per module.** Standard OBD values are built in; GM
  parameters can be added by hand in `custom-parameters.json`.
- **Manufacturer-specific code descriptions.** Generic SAE codes are described; for GM
  codes (P1xxx, most B/C codes) the app shows the code and category only.
- **Pre-1996 ALDL vehicles**, which J2534 interfaces don't support.

## Tested vs. untested

Everything was developed and tested against the built-in simulator (36 automated tests
exercise the real protocol code: Class 2 headers, CAN IDs, ISO-TP segmentation, GMLAN
UUDT code reports). **Nothing has been run against a real VCX Nano or vehicle yet.**
Things that most need checking on real hardware:

1. The driver loads and opens the device (Connect page shows firmware and battery volts).
2. Automatic detection picks the right network, and the VIN reads.
3. Module scan finds the modules you expect. Module names are best guesses from the
   address; the address is always shown.
4. Module code reading. The request formats used (`$19` on Class 2, `$A9 81` on GMLAN)
   and the status bit meanings come from public descriptions and should be compared
   with a known-good tool once.
5. Single wire and mid speed CAN, which depend on the interface supporting the J2534-2
   pin-switched protocols.

If something doesn't answer, tick "Write every bus message to the session log", repeat
it, and send the log from `Documents\VCX Diag\Logs`.

## Building

Requires the .NET 8 SDK.

    cd windows
    dotnet test tests/VcxDiag.Core.Tests
    dotnet publish src/VcxDiag.App -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/x86

The installer is built from `installer/VcxDiag.iss` with Inno Setup 6. The GitHub
Actions workflow "Windows app" does all of this and uploads `VcxDiag.exe` and
`VcxDiag-Setup.exe` as build artifacts; pushing a tag like `v1.0.0` also publishes them
as a release.

Layout: `src/VcxDiag.Core` (J2534, protocols, decoding, simulator; no UI),
`src/VcxDiag.App` (WPF user interface), `tests/VcxDiag.Core.Tests`.
