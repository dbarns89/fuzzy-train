namespace VcxDiag.Core.Obd;

public enum DtcSource
{
    Stored,      // OBD mode 03 / GM confirmed
    Pending,     // OBD mode 07
    Permanent,   // OBD mode 0A
    Module,      // GM enhanced read ($19 Class 2 / $A9 GMLAN)
}

/// <summary>A diagnostic trouble code such as P0420 or B1000-13.</summary>
public sealed record Dtc
{
    public required string Code { get; init; }
    public required string Module { get; init; }
    public uint ModuleAddress { get; init; }
    public DtcSource Source { get; init; }
    /// <summary>GMLAN failure type byte ("symptom"), shown as the -XX suffix.</summary>
    public byte? FailureType { get; init; }
    public byte? StatusByte { get; init; }
    public string StatusText { get; init; } = "";

    public string FullCode => FailureType is byte f && f != 0 ? $"{Code}-{f:X2}" : Code;
    public string Description => DtcDescriptions.Describe(Code);
    public string FailureTypeText => FailureType is byte f ? FailureTypes.Describe(f) : "";

    /// <summary>Decodes the two-byte SAE J2012 form. Returns null for 00 00 (no code).</summary>
    public static string? DecodeCode(byte hi, byte lo)
    {
        if (hi == 0 && lo == 0) return null;
        var system = "PCBU"[hi >> 6];
        return $"{system}{(hi >> 4) & 0x3}{hi & 0x0F:X1}{lo:X2}";
    }

    /// <summary>Inverse of <see cref="DecodeCode"/>, e.g. "P0420" -> 04 20.</summary>
    public static (byte Hi, byte Lo) EncodeCode(string code)
    {
        int sys = "PCBU".IndexOf(char.ToUpperInvariant(code[0]));
        if (sys < 0 || code.Length != 5) throw new FormatException(code);
        int d1 = code[1] - '0';
        int rest = Convert.ToInt32(code[2..], 16);
        return ((byte)(sys << 6 | d1 << 4 | rest >> 8), (byte)rest);
    }
}

/// <summary>SAE J2012 / ISO 15031-6 failure type bytes (the "-13" in "B1000-13").</summary>
public static class FailureTypes
{
    private static readonly Dictionary<byte, string> Table = new()
    {
        [0x00] = "No sub-type information", [0x01] = "General electrical failure", [0x02] = "General signal failure",
        [0x03] = "FM/PWM failure", [0x04] = "System internal failure", [0x05] = "System programming failure",
        [0x06] = "Algorithm based failure", [0x07] = "Mechanical failure", [0x08] = "Bus signal / message failure",
        [0x09] = "Component failure",
        [0x11] = "Circuit short to ground", [0x12] = "Circuit short to battery", [0x13] = "Circuit open",
        [0x14] = "Circuit short to ground or open", [0x15] = "Circuit short to battery or open",
        [0x16] = "Circuit voltage below threshold", [0x17] = "Circuit voltage above threshold",
        [0x18] = "Circuit current below threshold", [0x19] = "Circuit current above threshold",
        [0x1A] = "Circuit resistance below threshold", [0x1B] = "Circuit resistance above threshold",
        [0x1C] = "Circuit voltage out of range", [0x1D] = "Circuit current out of range",
        [0x1E] = "Circuit resistance out of range", [0x1F] = "Circuit intermittent",
        [0x21] = "Signal amplitude below minimum", [0x22] = "Signal amplitude above maximum",
        [0x23] = "Signal stuck low", [0x24] = "Signal stuck high", [0x25] = "Signal shape / waveform failure",
        [0x26] = "Signal rate of change below threshold", [0x27] = "Signal rate of change above threshold",
        [0x28] = "Signal bias level out of range / zero adjustment failure", [0x29] = "Signal invalid",
        [0x2F] = "Signal erratic", [0x31] = "No signal", [0x32] = "Signal low time below minimum",
        [0x33] = "Signal low time above maximum", [0x34] = "Signal high time below minimum",
        [0x35] = "Signal high time above maximum", [0x36] = "Signal frequency too low",
        [0x37] = "Signal frequency too high", [0x38] = "Signal frequency incorrect",
        [0x39] = "Incorrect number of pulses", [0x41] = "General checksum failure", [0x42] = "General memory failure",
        [0x43] = "Special memory failure", [0x44] = "Data memory failure", [0x45] = "Program memory failure",
        [0x46] = "Calibration / parameter memory failure", [0x47] = "Watchdog / safety processor failure",
        [0x48] = "Supervision software failure", [0x49] = "Internal electronic failure",
        [0x4A] = "Incorrect component installed", [0x4B] = "Over temperature",
        [0x51] = "Not programmed", [0x52] = "Not activated", [0x53] = "Deactivated", [0x54] = "Missing calibration",
        [0x55] = "Not configured", [0x61] = "Signal calculation failure", [0x62] = "Signal compare failure",
        [0x63] = "Circuit / component protection time-out", [0x64] = "Signal plausibility failure",
        [0x65] = "Signal has too few transitions / events", [0x66] = "Signal has too many transitions / events",
        [0x67] = "Signal incorrect after event", [0x68] = "Event information",
        [0x71] = "Actuator stuck", [0x72] = "Actuator stuck open", [0x73] = "Actuator stuck closed",
        [0x74] = "Actuator slipping", [0x75] = "Emergency position not reachable",
        [0x76] = "Wrong mounting position", [0x77] = "Commanded position not reachable",
        [0x78] = "Alignment or adjustment incorrect", [0x79] = "Mechanical linkage failure",
        [0x7A] = "Fluid leak or seal failure", [0x7B] = "Low fluid level",
        [0x81] = "Invalid serial data received", [0x82] = "Alive / sequence counter incorrect",
        [0x83] = "Signal protection value incorrect", [0x84] = "Signal below allowable range",
        [0x85] = "Signal above allowable range", [0x86] = "Signal invalid", [0x87] = "Missing message",
        [0x88] = "Bus off", [0x8F] = "Erratic", [0x91] = "Parametric", [0x92] = "Performance or incorrect operation",
        [0x93] = "No operation", [0x94] = "Unexpected operation", [0x95] = "Incorrect assembly",
        [0x96] = "Component internal failure", [0x97] = "Component or system operation obstructed or blocked",
        [0x98] = "Component or system over temperature",
    };

    public static string Describe(byte ftb) => Table.TryGetValue(ftb, out var s) ? s : $"Failure type {ftb:X2}";
}
