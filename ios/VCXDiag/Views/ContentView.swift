import SwiftUI

struct ContentView: View {
    @EnvironmentObject var session: DiagSession

    var body: some View {
        TabView {
            ConnectView()
                .tabItem { Label("Connect", systemImage: "cable.connector") }
            ModulesView()
                .tabItem { Label("Codes", systemImage: "exclamationmark.triangle") }
            LiveDataView()
                .tabItem { Label("Live data", systemImage: "gauge.with.dots.needle.33percent") }
            LogView()
                .tabItem { Label("Log", systemImage: "list.bullet.rectangle") }
        }
        .overlay(alignment: .top) {
            if let err = session.error {
                Text(err)
                    .font(.footnote)
                    .padding(8)
                    .background(.red.opacity(0.9), in: RoundedRectangle(cornerRadius: 8))
                    .foregroundStyle(.white)
                    .padding(.top, 4)
                    .onTapGesture { session.error = nil }
            }
        }
    }
}

struct ConnectView: View {
    @EnvironmentObject var session: DiagSession

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Bridge PC address", text: $session.bridgeHost)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    if session.phase == .disconnected || session.phase == .connecting {
                        Button("Connect to VCX Nano") { Task { await session.connectBridge() } }
                            .disabled(session.busy)
                    } else {
                        Button("Disconnect", role: .destructive) { Task { await session.disconnect() } }
                    }
                } header: {
                    Text("Adapter")
                } footer: {
                    Text("Run vcx_bridge.py on the PC the VCX Nano is plugged into, then enter the address it prints.")
                }

                if let a = session.adapter {
                    Section("Adapter info") {
                        LabeledContent("Firmware", value: a.firmware)
                        LabeledContent("Driver", value: a.driver)
                        LabeledContent("Battery", value: String(format: "%.1f V", a.batteryVolts))
                    }
                }

                if session.phase == .connected || session.phase == .busOpen {
                    Section("Vehicle bus") {
                        Picker("Protocol", selection: $session.bus) {
                            ForEach(BusProtocol.allCases) { Text($0.displayName).tag($0) }
                        }
                        Button(session.phase == .busOpen ? "Reopen bus" : "Open bus") {
                            Task { await session.openBus(session.bus) }
                        }
                        .disabled(session.busy)
                        Text("Class 2 (J1850 VPW) for most GM vehicles up to 2007. HS-CAN / GMLAN for 2008 and newer.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }

                if session.phase == .busOpen {
                    Section("Vehicle") {
                        LabeledContent("VIN", value: session.vin ?? "Not read")
                        Button("Read VIN") { Task { await session.readVIN() } }
                    }
                }
            }
            .navigationTitle("VCX Diag")
            .overlay { if session.busy { ProgressView() } }
        }
    }
}

struct ModulesView: View {
    @EnvironmentObject var session: DiagSession
    @State private var confirmClear = false

    var body: some View {
        NavigationStack {
            List {
                Section("Modules found") {
                    if session.modules.isEmpty {
                        Text("Run a scan to find modules.").foregroundStyle(.secondary)
                    }
                    ForEach(session.modules) { m in
                        HStack {
                            Text(m.name)
                            Spacer()
                            Text(String(format: "$%02X", m.address)).monospaced().foregroundStyle(.secondary)
                        }
                    }
                }
                Section("Trouble codes (\(session.dtcs.count))") {
                    ForEach(session.dtcs) { d in
                        VStack(alignment: .leading, spacing: 2) {
                            HStack {
                                Text(d.code).font(.headline.monospaced())
                                Spacer()
                                Text(d.module).font(.caption).foregroundStyle(.secondary)
                            }
                            Text(d.description).font(.subheadline)
                            if let s = d.status { Text(s).font(.caption).foregroundStyle(.orange) }
                        }
                    }
                }
            }
            .navigationTitle("Codes")
            .toolbar {
                ToolbarItemGroup {
                    Button("Scan") { Task { await session.scanAllModules() } }
                    Button("Clear", role: .destructive) { confirmClear = true }
                        .disabled(session.dtcs.isEmpty)
                }
            }
            .disabled(session.phase != .busOpen || session.busy)
            .overlay { if session.busy { ProgressView() } }
            .confirmationDialog("Clear all codes?", isPresented: $confirmClear, titleVisibility: .visible) {
                Button("Clear codes", role: .destructive) { Task { await session.clearAllDTCs() } }
            } message: {
                Text("Key on, engine off. This also resets emissions readiness monitors.")
            }
        }
    }
}

struct LiveDataView: View {
    @EnvironmentObject var session: DiagSession
    @State private var selected: Set<UInt8> = [0x0C, 0x05, 0x0D, 0x11]
    @State private var running = false

    var body: some View {
        NavigationStack {
            List {
                ForEach(PID.common) { pid in
                    HStack {
                        Image(systemName: selected.contains(pid.id) ? "checkmark.circle.fill" : "circle")
                            .foregroundStyle(.tint)
                        Text(pid.name)
                        Spacer()
                        if let v = session.liveValues[pid.id], selected.contains(pid.id) {
                            Text(String(format: "%.1f %@", v, pid.unit)).monospacedDigit()
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture {
                        if selected.contains(pid.id) { selected.remove(pid.id) } else { selected.insert(pid.id) }
                        if running { session.startLive(PID.common.filter { selected.contains($0.id) }) }
                    }
                }
            }
            .navigationTitle("Live data")
            .toolbar {
                Button(running ? "Stop" : "Start") {
                    running.toggle()
                    if running { session.startLive(PID.common.filter { selected.contains($0.id) }) }
                    else { session.stopLive() }
                }
                .disabled(session.phase != .busOpen)
            }
            .onDisappear { session.stopLive(); running = false }
        }
    }
}

struct LogView: View {
    @EnvironmentObject var session: DiagSession

    var body: some View {
        NavigationStack {
            List(Array(session.log.enumerated().reversed()), id: \.offset) { _, line in
                Text(line).font(.caption.monospaced())
            }
            .navigationTitle("Log")
        }
    }
}
