namespace VcxDiag.Core.Obd;

public sealed record MonitorState(string Name, bool Supported, bool Complete)
{
    public string State => !Supported ? "Not supported" : Complete ? "Complete" : "Not complete";
}

public sealed record ReadinessStatus(bool MilOn, int DtcCount, bool Diesel, IReadOnlyList<MonitorState> Monitors)
{
    /// <summary>Decodes mode $01 PID $01 (4 data bytes A B C D).</summary>
    public static ReadinessStatus Decode(ReadOnlySpan<byte> d)
    {
        bool diesel = (d[1] & 0x08) != 0;
        var list = new List<MonitorState>
        {
            new("Misfire", (d[1] & 0x01) != 0, (d[1] & 0x10) == 0),
            new("Fuel system", (d[1] & 0x02) != 0, (d[1] & 0x20) == 0),
            new("Comprehensive components", (d[1] & 0x04) != 0, (d[1] & 0x40) == 0),
        };
        string[] names = diesel
            ? new[] { "NMHC catalyst", "NOx / SCR aftertreatment", "(reserved)", "Boost pressure", "(reserved)", "Exhaust gas sensor", "PM filter", "EGR / VVT system" }
            : new[] { "Catalyst", "Heated catalyst", "Evaporative system", "Secondary air system", "A/C refrigerant", "Oxygen sensor", "Oxygen sensor heater", "EGR / VVT system" };
        for (int bit = 0; bit < 8; bit++)
        {
            if (names[bit].StartsWith('(')) continue;
            list.Add(new(names[bit], (d[2] & (1 << bit)) != 0, (d[3] & (1 << bit)) == 0));
        }
        return new ReadinessStatus((d[0] & 0x80) != 0, d[0] & 0x7F, diesel, list);
    }
}

/// <summary>One on-board monitor test result (OBD mode $06, CAN format).</summary>
public sealed record TestResult(byte Mid, byte Tid, byte UnitScaling, double Value, double Min, double Max, string Unit)
{
    public string MonitorName => Mode06.MidName(Mid);
    public bool Passed => Value >= Min && Value <= Max;
    public string Verdict => Passed ? "Pass" : "FAIL";
}

public static class Mode06
{
    public static string MidName(byte mid) => mid switch
    {
        >= 0x01 and <= 0x10 => $"Oxygen sensor monitor {O2(mid - 0x01)}",
        0x21 => "Catalyst monitor bank 1", 0x22 => "Catalyst monitor bank 2",
        0x23 => "Catalyst monitor bank 3", 0x24 => "Catalyst monitor bank 4",
        0x31 => "EGR monitor bank 1", 0x32 => "EGR monitor bank 2",
        0x35 => "VVT monitor bank 1", 0x36 => "VVT monitor bank 2",
        0x39 => "EVAP monitor (cap off)", 0x3A => "EVAP monitor (0.090\")",
        0x3B => "EVAP monitor (0.040\")", 0x3C => "EVAP monitor (0.020\")", 0x3D => "Purge flow monitor",
        >= 0x41 and <= 0x50 => $"Oxygen sensor heater monitor {O2(mid - 0x41)}",
        0x61 => "Heated catalyst monitor bank 1", 0x62 => "Heated catalyst monitor bank 2",
        0x71 => "Secondary air monitor 1", 0x72 => "Secondary air monitor 2",
        0x81 => "Fuel system monitor bank 1", 0x82 => "Fuel system monitor bank 2",
        0xA1 => "Misfire monitor general data",
        >= 0xA2 and <= 0xAD => $"Misfire cylinder {mid - 0xA1}",
        0xB0 => "PM filter monitor bank 1", 0xB1 => "PM filter monitor bank 2",
        _ => $"Monitor ${mid:X2}",
    };

    private static string O2(int i) => $"B{i / 4 + 1}S{i % 4 + 1}";

    // Unit and scaling IDs (SAE J1979 appendix). (scale, offset, unit)
    private static readonly Dictionary<byte, (double Scale, double Offset, string Unit)> Uas = new()
    {
        [0x01] = (1, 0, ""), [0x02] = (0.1, 0, ""), [0x03] = (0.01, 0, ""), [0x04] = (0.001, 0, ""),
        [0x05] = (0.0000305, 0, ""), [0x06] = (0.000305, 0, ""), [0x07] = (0.25, 0, "rpm"),
        [0x08] = (0.01, 0, "km/h"), [0x09] = (1, 0, "km/h"), [0x0A] = (0.122, 0, "mV"),
        [0x0B] = (0.001, 0, "V"), [0x0C] = (0.01, 0, "V"), [0x0D] = (0.00390625, 0, "mA"),
        [0x0E] = (0.001, 0, "A"), [0x0F] = (0.01, 0, "A"), [0x10] = (1, 0, "ms"), [0x11] = (100, 0, "ms"),
        [0x12] = (1, 0, "s"), [0x13] = (1, 0, "mΩ"), [0x14] = (1, 0, "Ω"), [0x15] = (1, 0, "kΩ"),
        [0x16] = (0.1, -40, "°C"), [0x17] = (0.01, 0, "kPa"), [0x18] = (0.0117, 0, "kPa"),
        [0x19] = (0.079, 0, "kPa"), [0x1A] = (1, 0, "kPa"), [0x1B] = (10, 0, "kPa"), [0x1C] = (0.01, 0, "°"),
        [0x1D] = (0.5, 0, "°"), [0x1E] = (0.0000305, 0, "λ"), [0x1F] = (0.05, 0, "A/F"),
        [0x20] = (0.0039062, 0, ""), [0x21] = (1, 0, "mHz"), [0x22] = (1, 0, "Hz"), [0x23] = (1, 0, "kHz"),
        [0x24] = (1, 0, "counts"), [0x25] = (1, 0, "km"), [0x26] = (0.1, 0, "mV/ms"), [0x27] = (0.01, 0, "g/s"),
        [0x28] = (1, 0, "g/s"), [0x29] = (0.25, 0, "Pa/s"), [0x2A] = (0.001, 0, "kg/h"), [0x2B] = (1, 0, "switches"),
        [0x2C] = (0.01, 0, "g/cyl"), [0x2D] = (0.01, 0, "mg/stroke"), [0x2F] = (0.01, 0, "%"),
        [0x30] = (0.001526, 0, "%"), [0x31] = (0.001, 0, "L"), [0x34] = (1, 0, "min"), [0x35] = (10, 0, "ms"),
        [0x81] = (1, 0, ""), [0x82] = (0.1, 0, ""), [0x83] = (0.01, 0, ""), [0x84] = (0.001, 0, ""),
        [0x8A] = (0.122, 0, "mV"), [0x8B] = (0.001, 0, "V"), [0x8C] = (0.01, 0, "V"), [0x8D] = (0.00390625, 0, "mA"),
        [0x8E] = (0.001, 0, "A"), [0x90] = (1, 0, "ms"), [0x96] = (0.1, 0, "°C"), [0x9C] = (0.01, 0, "°"),
        [0x9D] = (0.5, 0, "°"), [0xAF] = (0.01, 0, "%"), [0xFC] = (0.01, 0, "kPa"), [0xFD] = (0.001, 0, "kPa"),
        [0xFE] = (0.25, 0, "Pa"),
    };

    public static (double Value, string Unit) Scale(byte uas, ushort raw)
    {
        double v = uas >= 0x80 ? (short)raw : raw;
        return Uas.TryGetValue(uas, out var s) ? (v * s.Scale + s.Offset, s.Unit) : (v, $"raw (UAS ${uas:X2})");
    }

    /// <summary>Parses a CAN mode $46 reply body (after the 0x46 byte): records of MID TID UAS V V MIN MIN MAX MAX.</summary>
    public static IEnumerable<TestResult> ParseCan(ReadOnlySpan<byte> body)
    {
        var list = new List<TestResult>();
        for (int i = 0; i + 9 <= body.Length; i += 9)
        {
            byte mid = body[i], tid = body[i + 1], uas = body[i + 2];
            var (v, unit) = Scale(uas, (ushort)(body[i + 3] << 8 | body[i + 4]));
            var (mn, _) = Scale(uas, (ushort)(body[i + 5] << 8 | body[i + 6]));
            var (mx, _) = Scale(uas, (ushort)(body[i + 7] << 8 | body[i + 8]));
            list.Add(new TestResult(mid, tid, uas, v, mn, mx, unit));
        }
        return list;
    }
}
