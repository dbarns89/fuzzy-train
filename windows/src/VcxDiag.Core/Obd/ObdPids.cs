namespace VcxDiag.Core.Obd;

public enum UnitKind { None, Temperature, Speed, Pressure, Distance, MassFlow, Volume }

/// <summary>One live value decoded from an OBD mode $01/$02 PID. Some PIDs carry several values.</summary>
public sealed record ObdParameter(byte Pid, string Key, string Name, string Unit, UnitKind Kind, Func<byte[], double> Decode)
{
    public int Bytes => ObdPids.Length(Pid);
    public override string ToString() => Name;
}

/// <summary>SAE J1979 mode $01 PIDs (public standard formulas).</summary>
public static class ObdPids
{
    private static double A(byte[] d) => d[0];
    private static double B(byte[] d) => d[1];
    private static double AB(byte[] d) => d[0] * 256.0 + d[1];
    private static double CD(byte[] d) => d[2] * 256.0 + d[3];
    private static double Pct(byte v) => v * 100.0 / 255.0;
    private static double Trim(byte v) => (v - 128) * 100.0 / 128.0;

    /// <summary>Payload length of each PID in bytes (needed to split multi-PID CAN replies).</summary>
    private static readonly Dictionary<byte, int> Lengths = new()
    {
        [0x00] = 4, [0x01] = 4, [0x02] = 2, [0x03] = 2, [0x04] = 1, [0x05] = 1, [0x06] = 1, [0x07] = 1, [0x08] = 1,
        [0x09] = 1, [0x0A] = 1, [0x0B] = 1, [0x0C] = 2, [0x0D] = 1, [0x0E] = 1, [0x0F] = 1, [0x10] = 2, [0x11] = 1,
        [0x12] = 1, [0x13] = 1, [0x14] = 2, [0x15] = 2, [0x16] = 2, [0x17] = 2, [0x18] = 2, [0x19] = 2, [0x1A] = 2,
        [0x1B] = 2, [0x1C] = 1, [0x1D] = 1, [0x1E] = 1, [0x1F] = 2, [0x20] = 4, [0x21] = 2, [0x22] = 2, [0x23] = 2,
        [0x24] = 4, [0x25] = 4, [0x26] = 4, [0x27] = 4, [0x28] = 4, [0x29] = 4, [0x2A] = 4, [0x2B] = 4, [0x2C] = 1,
        [0x2D] = 1, [0x2E] = 1, [0x2F] = 1, [0x30] = 1, [0x31] = 2, [0x32] = 2, [0x33] = 1, [0x34] = 4, [0x35] = 4,
        [0x36] = 4, [0x37] = 4, [0x38] = 4, [0x39] = 4, [0x3A] = 4, [0x3B] = 4, [0x3C] = 2, [0x3D] = 2, [0x3E] = 2,
        [0x3F] = 2, [0x40] = 4, [0x41] = 4, [0x42] = 2, [0x43] = 2, [0x44] = 2, [0x45] = 1, [0x46] = 1, [0x47] = 1,
        [0x48] = 1, [0x49] = 1, [0x4A] = 1, [0x4B] = 1, [0x4C] = 1, [0x4D] = 2, [0x4E] = 2, [0x4F] = 4, [0x50] = 4,
        [0x51] = 1, [0x52] = 1, [0x53] = 2, [0x54] = 2, [0x55] = 2, [0x56] = 2, [0x57] = 2, [0x58] = 2, [0x59] = 2,
        [0x5A] = 1, [0x5B] = 1, [0x5C] = 1, [0x5D] = 2, [0x5E] = 2, [0x5F] = 1, [0x60] = 4, [0x61] = 1, [0x62] = 1,
        [0x63] = 2, [0x80] = 4, [0xA0] = 4, [0xA6] = 4, [0xC0] = 4,
    };

    public static int Length(byte pid) => Lengths.TryGetValue(pid, out var n) ? n : 0;

    public static bool IsSupportBitmap(byte pid) => pid % 0x20 == 0;

    public static readonly IReadOnlyList<ObdParameter> All = Build();

    public static IEnumerable<ObdParameter> ForPid(byte pid) => All.Where(p => p.Pid == pid);

    private static List<ObdParameter> Build()
    {
        var l = new List<ObdParameter>
        {
            new(0x04, "04", "Calculated engine load", "%", UnitKind.None, d => Pct(d[0])),
            new(0x05, "05", "Engine coolant temperature", "°C", UnitKind.Temperature, d => A(d) - 40),
            new(0x06, "06", "Short term fuel trim bank 1", "%", UnitKind.None, d => Trim(d[0])),
            new(0x07, "07", "Long term fuel trim bank 1", "%", UnitKind.None, d => Trim(d[0])),
            new(0x08, "08", "Short term fuel trim bank 2", "%", UnitKind.None, d => Trim(d[0])),
            new(0x09, "09", "Long term fuel trim bank 2", "%", UnitKind.None, d => Trim(d[0])),
            new(0x0A, "0A", "Fuel pressure (gauge)", "kPa", UnitKind.Pressure, d => A(d) * 3),
            new(0x0B, "0B", "Intake manifold absolute pressure", "kPa", UnitKind.Pressure, A),
            new(0x0C, "0C", "Engine speed", "rpm", UnitKind.None, d => AB(d) / 4),
            new(0x0D, "0D", "Vehicle speed", "km/h", UnitKind.Speed, A),
            new(0x0E, "0E", "Ignition timing advance (cyl 1)", "°", UnitKind.None, d => A(d) / 2 - 64),
            new(0x0F, "0F", "Intake air temperature", "°C", UnitKind.Temperature, d => A(d) - 40),
            new(0x10, "10", "Mass air flow rate", "g/s", UnitKind.MassFlow, d => AB(d) / 100),
            new(0x11, "11", "Throttle position", "%", UnitKind.None, d => Pct(d[0])),
            new(0x1F, "1F", "Run time since engine start", "s", UnitKind.None, AB),
            new(0x21, "21", "Distance with MIL on", "km", UnitKind.Distance, AB),
            new(0x22, "22", "Fuel rail pressure (relative to vacuum)", "kPa", UnitKind.Pressure, d => AB(d) * 0.079),
            new(0x23, "23", "Fuel rail gauge pressure", "kPa", UnitKind.Pressure, d => AB(d) * 10),
            new(0x2C, "2C", "Commanded EGR", "%", UnitKind.None, d => Pct(d[0])),
            new(0x2D, "2D", "EGR error", "%", UnitKind.None, d => Trim(d[0])),
            new(0x2E, "2E", "Commanded evaporative purge", "%", UnitKind.None, d => Pct(d[0])),
            new(0x2F, "2F", "Fuel tank level", "%", UnitKind.None, d => Pct(d[0])),
            new(0x30, "30", "Warm-ups since codes cleared", "", UnitKind.None, A),
            new(0x31, "31", "Distance since codes cleared", "km", UnitKind.Distance, AB),
            new(0x32, "32", "EVAP system vapor pressure", "Pa", UnitKind.None, d => (short)(d[0] << 8 | d[1]) / 4.0),
            new(0x33, "33", "Barometric pressure", "kPa", UnitKind.Pressure, A),
            new(0x3C, "3C", "Catalyst temperature B1S1", "°C", UnitKind.Temperature, d => AB(d) / 10 - 40),
            new(0x3D, "3D", "Catalyst temperature B2S1", "°C", UnitKind.Temperature, d => AB(d) / 10 - 40),
            new(0x3E, "3E", "Catalyst temperature B1S2", "°C", UnitKind.Temperature, d => AB(d) / 10 - 40),
            new(0x3F, "3F", "Catalyst temperature B2S2", "°C", UnitKind.Temperature, d => AB(d) / 10 - 40),
            new(0x42, "42", "Control module voltage", "V", UnitKind.None, d => AB(d) / 1000),
            new(0x43, "43", "Absolute load value", "%", UnitKind.None, d => AB(d) * 100 / 255),
            new(0x44, "44", "Commanded air-fuel equivalence ratio", "λ", UnitKind.None, d => AB(d) * 2 / 65536),
            new(0x45, "45", "Relative throttle position", "%", UnitKind.None, d => Pct(d[0])),
            new(0x46, "46", "Ambient air temperature", "°C", UnitKind.Temperature, d => A(d) - 40),
            new(0x47, "47", "Absolute throttle position B", "%", UnitKind.None, d => Pct(d[0])),
            new(0x48, "48", "Absolute throttle position C", "%", UnitKind.None, d => Pct(d[0])),
            new(0x49, "49", "Accelerator pedal position D", "%", UnitKind.None, d => Pct(d[0])),
            new(0x4A, "4A", "Accelerator pedal position E", "%", UnitKind.None, d => Pct(d[0])),
            new(0x4B, "4B", "Accelerator pedal position F", "%", UnitKind.None, d => Pct(d[0])),
            new(0x4C, "4C", "Commanded throttle actuator", "%", UnitKind.None, d => Pct(d[0])),
            new(0x4D, "4D", "Time run with MIL on", "min", UnitKind.None, AB),
            new(0x4E, "4E", "Time since codes cleared", "min", UnitKind.None, AB),
            new(0x51, "51", "Fuel type", "", UnitKind.None, A),
            new(0x52, "52", "Ethanol fuel", "%", UnitKind.None, d => Pct(d[0])),
            new(0x59, "59", "Fuel rail absolute pressure", "kPa", UnitKind.Pressure, d => AB(d) * 10),
            new(0x5A, "5A", "Relative accelerator pedal position", "%", UnitKind.None, d => Pct(d[0])),
            new(0x5B, "5B", "Hybrid battery pack remaining life", "%", UnitKind.None, d => Pct(d[0])),
            new(0x5C, "5C", "Engine oil temperature", "°C", UnitKind.Temperature, d => A(d) - 40),
            new(0x5D, "5D", "Fuel injection timing", "°", UnitKind.None, d => (AB(d) - 26880) / 128),
            new(0x5E, "5E", "Engine fuel rate", "L/h", UnitKind.Volume, d => AB(d) / 20),
            new(0x61, "61", "Driver's demand engine torque", "%", UnitKind.None, d => A(d) - 125),
            new(0x62, "62", "Actual engine torque", "%", UnitKind.None, d => A(d) - 125),
            new(0x63, "63", "Engine reference torque", "Nm", UnitKind.None, AB),
        };

        // Narrowband O2 sensors: voltage + short term trim. PIDs 14-1B = B1S1..B2S4 (or B4S2 layout).
        string[] o2 = { "B1S1", "B1S2", "B1S3", "B1S4", "B2S1", "B2S2", "B2S3", "B2S4" };
        for (int i = 0; i < 8; i++)
        {
            byte pid = (byte)(0x14 + i);
            l.Add(new(pid, $"{pid:X2}.V", $"O2 sensor {o2[i]} voltage", "V", UnitKind.None, d => A(d) / 200));
            l.Add(new(pid, $"{pid:X2}.T", $"O2 sensor {o2[i]} short term trim", "%", UnitKind.None,
                d => d[1] == 0xFF ? double.NaN : Trim(d[1])));
        }
        // Wide-range O2 sensors (equivalence ratio + current). PIDs 34-3B.
        for (int i = 0; i < 8; i++)
        {
            byte pid = (byte)(0x34 + i);
            l.Add(new(pid, $"{pid:X2}.L", $"Wideband O2 {o2[i]} lambda", "λ", UnitKind.None, d => AB(d) * 2 / 65536));
            l.Add(new(pid, $"{pid:X2}.I", $"Wideband O2 {o2[i]} current", "mA", UnitKind.None, d => CD(d) / 256 - 128));
        }
        return l.OrderBy(p => p.Pid).ThenBy(p => p.Key).ToList();
    }

    /// <summary>Decodes a "PIDs supported" bitmap reply (PID 00/20/40...).</summary>
    public static IEnumerable<byte> DecodeSupportBitmap(byte basePid, ReadOnlySpan<byte> bitmap)
    {
        var list = new List<byte>();
        for (int i = 0; i < 32 && i / 8 < bitmap.Length; i++)
            if ((bitmap[i / 8] & (0x80 >> (i % 8))) != 0) list.Add((byte)(basePid + i + 1));
        return list;
    }

    /// <summary>
    /// Splits a mode $41/$42 payload (without the mode byte) into PID -> data.
    /// CAN replies can hold several PIDs in one message; freeze frame ($42) has a frame byte after each PID.
    /// </summary>
    public static Dictionary<byte, byte[]> SplitReply(ReadOnlySpan<byte> body, bool freezeFrame = false)
    {
        var map = new Dictionary<byte, byte[]>();
        int i = 0;
        while (i < body.Length)
        {
            byte pid = body[i++];
            if (freezeFrame) i++; // frame number
            int n = Length(pid);
            if (n == 0 || i + n > body.Length) break;
            map[pid] = body.Slice(i, n).ToArray();
            i += n;
        }
        return map;
    }
}

/// <summary>Converts metric values for display when the user picks US units.</summary>
public static class Units
{
    public static (double Value, string Unit) ToImperial(double v, string unit, UnitKind kind) => kind switch
    {
        UnitKind.Temperature => (v * 9 / 5 + 32, "°F"),
        UnitKind.Speed => (v * 0.621371, "mph"),
        UnitKind.Distance => (v * 0.621371, "mi"),
        UnitKind.Pressure => (v * 0.145038, "psi"),
        UnitKind.MassFlow => (v * 0.132277, "lb/min"),
        UnitKind.Volume => (v * 0.264172, "gal/h"),
        _ => (v, unit),
    };
}
