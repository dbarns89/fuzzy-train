namespace VcxDiag.Core.Obd;

/// <summary>
/// Generic SAE J2012 descriptions. Manufacturer-specific codes (P1xxx, B1xxx, C0xxx on GM,
/// etc.) vary by model, so the app only names their category and never guesses.
/// Users can add or override descriptions with a "dtc-descriptions.csv" file
/// (code,description per line) next to the program or in Documents\VCX Diag.
/// </summary>
public static class DtcDescriptions
{
    private static readonly Dictionary<string, string> User = new(StringComparer.OrdinalIgnoreCase);

    public static int LoadUserFile(string path)
    {
        if (!File.Exists(path)) return 0;
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var comma = t.IndexOf(',');
            if (comma < 4) continue;
            var code = t[..comma].Trim().Trim('"');
            var text = t[(comma + 1)..].Trim().Trim('"');
            if (code.Length >= 5) { User[code] = text; n++; }
        }
        return n;
    }

    public static string Describe(string code)
    {
        if (User.TryGetValue(code, out var u)) return u;
        if (Generic.TryGetValue(code, out var g)) return g;
        return Category(code);
    }

    public static bool HasSpecificText(string code) => User.ContainsKey(code) || Generic.ContainsKey(code);

    /// <summary>The SAE range a code falls in, used when there's no exact description.</summary>
    public static string Category(string code)
    {
        if (code.Length != 5) return "Unknown code";
        char sys = code[0];
        char d1 = code[1];
        bool mfr = d1 is '1' or '3' && sys is 'P' || d1 is '1' or '2' && sys is not 'P';
        string sysName = sys switch { 'P' => "Powertrain", 'C' => "Chassis", 'B' => "Body", 'U' => "Network", _ => "?" };
        if (mfr) return $"{sysName}: manufacturer specific code (see the vehicle's service information)";
        if (sys == 'P')
        {
            return code[2] switch
            {
                '0' or '1' or '2' => "Fuel and air metering",
                '3' => "Ignition system or misfire",
                '4' => "Auxiliary emission controls",
                '5' => "Vehicle speed, idle control or auxiliary inputs",
                '6' => "Computer and output circuits",
                '7' or '8' or '9' => "Transmission",
                'A' or 'B' or 'C' => "Hybrid propulsion",
                _ => "Powertrain",
            };
        }
        if (sys == 'U') return code[2] switch
        {
            '0' when code[3] is '0' or '1' or '2' or '3' => "Lost communication / network fault",
            '0' or '1' or '2' or '3' => "Network communication",
            '4' => "Invalid data received",
            _ => "Network",
        };
        return $"{sysName} (SAE generic)";
    }

    private static readonly Dictionary<string, string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P0010"] = "Intake camshaft position actuator circuit (bank 1)",
        ["P0011"] = "Intake camshaft position timing over-advanced (bank 1)",
        ["P0012"] = "Intake camshaft position timing over-retarded (bank 1)",
        ["P0013"] = "Exhaust camshaft position actuator circuit (bank 1)",
        ["P0014"] = "Exhaust camshaft position timing over-advanced (bank 1)",
        ["P0016"] = "Crankshaft / camshaft position correlation (bank 1 sensor A)",
        ["P0017"] = "Crankshaft / camshaft position correlation (bank 1 sensor B)",
        ["P0018"] = "Crankshaft / camshaft position correlation (bank 2 sensor A)",
        ["P0020"] = "Intake camshaft position actuator circuit (bank 2)",
        ["P0021"] = "Intake camshaft position timing over-advanced (bank 2)",
        ["P0030"] = "HO2S heater control circuit (bank 1 sensor 1)",
        ["P0036"] = "HO2S heater control circuit (bank 1 sensor 2)",
        ["P0050"] = "HO2S heater control circuit (bank 2 sensor 1)",
        ["P0056"] = "HO2S heater control circuit (bank 2 sensor 2)",
        ["P0068"] = "MAP / MAF - throttle position correlation",
        ["P0087"] = "Fuel rail / system pressure too low",
        ["P0088"] = "Fuel rail / system pressure too high",
        ["P0089"] = "Fuel pressure regulator performance",
        ["P0096"] = "Intake air temperature sensor 2 range/performance",
        ["P0097"] = "Intake air temperature sensor 2 circuit low",
        ["P0098"] = "Intake air temperature sensor 2 circuit high",
        ["P0100"] = "Mass air flow circuit",
        ["P0101"] = "Mass air flow circuit range/performance",
        ["P0102"] = "Mass air flow circuit low input",
        ["P0103"] = "Mass air flow circuit high input",
        ["P0106"] = "Manifold absolute pressure circuit range/performance",
        ["P0107"] = "Manifold absolute pressure circuit low input",
        ["P0108"] = "Manifold absolute pressure circuit high input",
        ["P0111"] = "Intake air temperature circuit range/performance",
        ["P0112"] = "Intake air temperature circuit low input",
        ["P0113"] = "Intake air temperature circuit high input",
        ["P0116"] = "Engine coolant temperature circuit range/performance",
        ["P0117"] = "Engine coolant temperature circuit low input",
        ["P0118"] = "Engine coolant temperature circuit high input",
        ["P0120"] = "Throttle position sensor A circuit",
        ["P0121"] = "Throttle position sensor A circuit range/performance",
        ["P0122"] = "Throttle position sensor A circuit low input",
        ["P0123"] = "Throttle position sensor A circuit high input",
        ["P0125"] = "Insufficient coolant temperature for closed loop fuel control",
        ["P0128"] = "Coolant temperature below thermostat regulating temperature",
        ["P0130"] = "O2 sensor circuit (bank 1 sensor 1)",
        ["P0131"] = "O2 sensor circuit low voltage (bank 1 sensor 1)",
        ["P0132"] = "O2 sensor circuit high voltage (bank 1 sensor 1)",
        ["P0133"] = "O2 sensor circuit slow response (bank 1 sensor 1)",
        ["P0134"] = "O2 sensor circuit no activity detected (bank 1 sensor 1)",
        ["P0135"] = "O2 sensor heater circuit (bank 1 sensor 1)",
        ["P0136"] = "O2 sensor circuit (bank 1 sensor 2)",
        ["P0137"] = "O2 sensor circuit low voltage (bank 1 sensor 2)",
        ["P0138"] = "O2 sensor circuit high voltage (bank 1 sensor 2)",
        ["P0140"] = "O2 sensor circuit no activity detected (bank 1 sensor 2)",
        ["P0141"] = "O2 sensor heater circuit (bank 1 sensor 2)",
        ["P0150"] = "O2 sensor circuit (bank 2 sensor 1)",
        ["P0151"] = "O2 sensor circuit low voltage (bank 2 sensor 1)",
        ["P0152"] = "O2 sensor circuit high voltage (bank 2 sensor 1)",
        ["P0153"] = "O2 sensor circuit slow response (bank 2 sensor 1)",
        ["P0154"] = "O2 sensor circuit no activity detected (bank 2 sensor 1)",
        ["P0155"] = "O2 sensor heater circuit (bank 2 sensor 1)",
        ["P0156"] = "O2 sensor circuit (bank 2 sensor 2)",
        ["P0157"] = "O2 sensor circuit low voltage (bank 2 sensor 2)",
        ["P0158"] = "O2 sensor circuit high voltage (bank 2 sensor 2)",
        ["P0160"] = "O2 sensor circuit no activity detected (bank 2 sensor 2)",
        ["P0161"] = "O2 sensor heater circuit (bank 2 sensor 2)",
        ["P0171"] = "System too lean (bank 1)",
        ["P0172"] = "System too rich (bank 1)",
        ["P0174"] = "System too lean (bank 2)",
        ["P0175"] = "System too rich (bank 2)",
        ["P0191"] = "Fuel rail pressure sensor circuit range/performance",
        ["P0200"] = "Injector circuit",
        ["P0201"] = "Injector circuit - cylinder 1",
        ["P0202"] = "Injector circuit - cylinder 2",
        ["P0203"] = "Injector circuit - cylinder 3",
        ["P0204"] = "Injector circuit - cylinder 4",
        ["P0205"] = "Injector circuit - cylinder 5",
        ["P0206"] = "Injector circuit - cylinder 6",
        ["P0207"] = "Injector circuit - cylinder 7",
        ["P0208"] = "Injector circuit - cylinder 8",
        ["P0218"] = "Transmission fluid over temperature condition",
        ["P0219"] = "Engine overspeed condition",
        ["P0220"] = "Throttle position sensor B circuit",
        ["P0221"] = "Throttle position sensor B circuit range/performance",
        ["P0222"] = "Throttle position sensor B circuit low input",
        ["P0223"] = "Throttle position sensor B circuit high input",
        ["P0230"] = "Fuel pump primary circuit",
        ["P0300"] = "Random / multiple cylinder misfire detected",
        ["P0301"] = "Cylinder 1 misfire detected",
        ["P0302"] = "Cylinder 2 misfire detected",
        ["P0303"] = "Cylinder 3 misfire detected",
        ["P0304"] = "Cylinder 4 misfire detected",
        ["P0305"] = "Cylinder 5 misfire detected",
        ["P0306"] = "Cylinder 6 misfire detected",
        ["P0307"] = "Cylinder 7 misfire detected",
        ["P0308"] = "Cylinder 8 misfire detected",
        ["P0315"] = "Crankshaft position system variation not learned",
        ["P0325"] = "Knock sensor 1 circuit (bank 1)",
        ["P0326"] = "Knock sensor 1 circuit range/performance (bank 1)",
        ["P0327"] = "Knock sensor 1 circuit low input (bank 1)",
        ["P0328"] = "Knock sensor 1 circuit high input (bank 1)",
        ["P0330"] = "Knock sensor 2 circuit (bank 2)",
        ["P0332"] = "Knock sensor 2 circuit low input (bank 2)",
        ["P0335"] = "Crankshaft position sensor A circuit",
        ["P0336"] = "Crankshaft position sensor A circuit range/performance",
        ["P0340"] = "Camshaft position sensor A circuit (bank 1)",
        ["P0341"] = "Camshaft position sensor A circuit range/performance (bank 1)",
        ["P0351"] = "Ignition coil A primary/secondary circuit",
        ["P0352"] = "Ignition coil B primary/secondary circuit",
        ["P0353"] = "Ignition coil C primary/secondary circuit",
        ["P0354"] = "Ignition coil D primary/secondary circuit",
        ["P0355"] = "Ignition coil E primary/secondary circuit",
        ["P0356"] = "Ignition coil F primary/secondary circuit",
        ["P0357"] = "Ignition coil G primary/secondary circuit",
        ["P0358"] = "Ignition coil H primary/secondary circuit",
        ["P0400"] = "Exhaust gas recirculation flow",
        ["P0401"] = "Exhaust gas recirculation flow insufficient",
        ["P0402"] = "Exhaust gas recirculation flow excessive",
        ["P0403"] = "Exhaust gas recirculation control circuit",
        ["P0404"] = "Exhaust gas recirculation control circuit range/performance",
        ["P0405"] = "EGR sensor A circuit low",
        ["P0410"] = "Secondary air injection system",
        ["P0411"] = "Secondary air injection system incorrect flow",
        ["P0420"] = "Catalyst system efficiency below threshold (bank 1)",
        ["P0430"] = "Catalyst system efficiency below threshold (bank 2)",
        ["P0440"] = "Evaporative emission system",
        ["P0441"] = "Evaporative emission system incorrect purge flow",
        ["P0442"] = "Evaporative emission system leak detected (small leak)",
        ["P0443"] = "Evaporative emission system purge control valve circuit",
        ["P0446"] = "Evaporative emission system vent control circuit",
        ["P0449"] = "Evaporative emission system vent valve/solenoid circuit",
        ["P0451"] = "Evaporative emission system pressure sensor range/performance",
        ["P0452"] = "Evaporative emission system pressure sensor low",
        ["P0453"] = "Evaporative emission system pressure sensor high",
        ["P0455"] = "Evaporative emission system leak detected (large leak)",
        ["P0456"] = "Evaporative emission system leak detected (very small leak)",
        ["P0461"] = "Fuel level sensor A circuit range/performance",
        ["P0462"] = "Fuel level sensor A circuit low",
        ["P0463"] = "Fuel level sensor A circuit high",
        ["P0480"] = "Fan 1 control circuit",
        ["P0496"] = "Evaporative emission system high purge flow",
        ["P0500"] = "Vehicle speed sensor A",
        ["P0501"] = "Vehicle speed sensor A range/performance",
        ["P0502"] = "Vehicle speed sensor A circuit low input",
        ["P0503"] = "Vehicle speed sensor A intermittent/erratic/high",
        ["P0505"] = "Idle air control system",
        ["P0506"] = "Idle air control system RPM lower than expected",
        ["P0507"] = "Idle air control system RPM higher than expected",
        ["P0520"] = "Engine oil pressure sensor/switch circuit",
        ["P0521"] = "Engine oil pressure sensor/switch range/performance",
        ["P0522"] = "Engine oil pressure sensor/switch low voltage",
        ["P0523"] = "Engine oil pressure sensor/switch high voltage",
        ["P0530"] = "A/C refrigerant pressure sensor A circuit",
        ["P0560"] = "System voltage",
        ["P0562"] = "System voltage low",
        ["P0563"] = "System voltage high",
        ["P0571"] = "Brake switch A circuit",
        ["P0600"] = "Serial communication link",
        ["P0601"] = "Internal control module memory checksum error",
        ["P0602"] = "Control module programming error",
        ["P0603"] = "Internal control module keep alive memory (KAM) error",
        ["P0604"] = "Internal control module random access memory (RAM) error",
        ["P0605"] = "Internal control module read only memory (ROM) error",
        ["P0606"] = "Control module processor",
        ["P0607"] = "Control module performance",
        ["P0620"] = "Generator control circuit",
        ["P0621"] = "Generator lamp L terminal control circuit",
        ["P0622"] = "Generator field F terminal control circuit",
        ["P0641"] = "Sensor reference voltage A circuit open",
        ["P0651"] = "Sensor reference voltage B circuit open",
        ["P0700"] = "Transmission control system (MIL request)",
        ["P0705"] = "Transmission range sensor circuit (PRNDL input)",
        ["P0706"] = "Transmission range sensor circuit range/performance",
        ["P0711"] = "Transmission fluid temperature sensor A circuit range/performance",
        ["P0712"] = "Transmission fluid temperature sensor A circuit low",
        ["P0713"] = "Transmission fluid temperature sensor A circuit high",
        ["P0715"] = "Input/turbine speed sensor A circuit",
        ["P0716"] = "Input/turbine speed sensor A circuit range/performance",
        ["P0717"] = "Input/turbine speed sensor A circuit no signal",
        ["P0720"] = "Output speed sensor circuit",
        ["P0721"] = "Output speed sensor circuit range/performance",
        ["P0722"] = "Output speed sensor circuit no signal",
        ["P0725"] = "Engine speed input circuit",
        ["P0730"] = "Incorrect gear ratio",
        ["P0740"] = "Torque converter clutch circuit",
        ["P0741"] = "Torque converter clutch circuit performance or stuck off",
        ["P0742"] = "Torque converter clutch circuit stuck on",
        ["P0748"] = "Pressure control solenoid A electrical",
        ["P0750"] = "Shift solenoid A",
        ["P0751"] = "Shift solenoid A performance or stuck off",
        ["P0752"] = "Shift solenoid A stuck on",
        ["P0753"] = "Shift solenoid A electrical",
        ["P0755"] = "Shift solenoid B",
        ["P0756"] = "Shift solenoid B performance or stuck off",
        ["P0757"] = "Shift solenoid B stuck on",
        ["P0758"] = "Shift solenoid B electrical",
        ["P0785"] = "Shift / timing solenoid",
        ["P0894"] = "Transmission component slipping",
        ["U0001"] = "High speed CAN communication bus",
        ["U0073"] = "Control module communication bus A off",
        ["U0100"] = "Lost communication with ECM/PCM A",
        ["U0101"] = "Lost communication with TCM",
        ["U0121"] = "Lost communication with anti-lock brake system (ABS) control module",
        ["U0140"] = "Lost communication with body control module",
        ["U0151"] = "Lost communication with restraints control module",
        ["U0155"] = "Lost communication with instrument panel cluster (IPC) control module",
        ["U0164"] = "Lost communication with HVAC control module",
        ["U0184"] = "Lost communication with radio",
        ["U0401"] = "Invalid data received from ECM/PCM A",
        ["U0402"] = "Invalid data received from TCM",
        ["U0415"] = "Invalid data received from anti-lock brake system (ABS) control module",
    };
}
