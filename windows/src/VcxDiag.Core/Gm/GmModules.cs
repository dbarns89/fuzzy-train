using VcxDiag.Core.Protocols;

namespace VcxDiag.Core.Gm;

/// <summary>A control module found on the vehicle.</summary>
public sealed class EcuModule
{
    /// <summary>Class 2 node address, or GMLAN/CAN physical request ID.</summary>
    public required uint Address { get; init; }
    public required BusKind Bus { get; init; }
    /// <summary>GMLAN diagnostic address from $1A B0 (matches the Class 2 node numbering).</summary>
    public byte? DiagAddress { get; set; }
    public string Name { get; set; } = "";
    public string FoundBy { get; set; } = "";
    public int? DtcCount { get; set; }

    public string AddressText => Bus == BusKind.Class2 ? $"${Address:X2}" : $"{Address:X3}";
    public override string ToString() => $"{Name} [{AddressText}]";
}

/// <summary>
/// Typical module names by node / diagnostic address (SAE J2178 numbering, which GMLAN
/// diagnostic addresses follow). Vehicles differ, so names are a best guess and the
/// address is always shown next to them.
/// </summary>
public static class GmModuleNames
{
    private static readonly Dictionary<uint, string> ByNode = new()
    {
        [0x10] = "Engine control (ECM/PCM)",
        [0x11] = "Engine control (ECM)",
        [0x12] = "Engine control 2",
        [0x17] = "Fuel system control",
        [0x18] = "Transmission control (TCM)",
        [0x19] = "Transmission control 2",
        [0x1A] = "Transfer case control",
        [0x28] = "Brake control (EBCM / ABS)",
        [0x29] = "Electronic suspension control",
        [0x2A] = "Steering control",
        [0x2C] = "Chassis expansion module",
        [0x30] = "Steering column lock",
        [0x32] = "Power steering",
        [0x38] = "Parking assist",
        [0x40] = "Body control (BCM)",
        [0x41] = "Body control 2",
        [0x42] = "Rear body module",
        [0x43] = "Driver door module",
        [0x45] = "Passenger door module",
        [0x58] = "Airbag / inflatable restraint (SDM)",
        [0x59] = "Passenger presence module",
        [0x60] = "Instrument cluster (IPC)",
        [0x61] = "Driver information center",
        [0x62] = "Head-up display",
        [0x64] = "Driver seat module",
        [0x68] = "Memory seat / mirror",
        [0x70] = "Remote keyless entry",
        [0x72] = "Theft deterrent",
        [0x80] = "Radio / audio",
        [0x81] = "Audio amplifier",
        [0x82] = "CD changer",
        [0x88] = "Rear seat entertainment",
        [0x90] = "Navigation",
        [0x91] = "Telematics (OnStar)",
        [0x93] = "Rear view camera",
        [0x97] = "Remote function actuator",
        [0x98] = "HVAC control",
        [0x99] = "HVAC control",
        [0x9A] = "Rear HVAC",
        [0xA0] = "Driver door switch",
        [0xA1] = "Liftgate module",
        [0xB0] = "Trailer / towing module",
        [0xC0] = "Gateway",
    };

    public static string ForNode(uint node) =>
        ByNode.TryGetValue(node, out var n) ? n : $"Module ${node:X2}";

    public static string ForCanRequestId(uint id) => id switch
    {
        0x7E0 => "Engine control (ECM)",
        0x7E1 => "Engine control 2 / hybrid",
        0x7E2 => "Transmission control (TCM)",
        0x7E3 => "Powertrain module 3",
        _ => $"Module {id:X3}",
    };

    /// <summary>Best name for a module: diagnostic address first, then the bus address.</summary>
    public static string Resolve(EcuModule m)
    {
        if (m.Bus == BusKind.Class2) return ForNode(m.Address);
        if (m.DiagAddress is byte d && ByNode.ContainsKey(d)) return ForNode(d);
        return ForCanRequestId(m.Address);
    }
}

/// <summary>Labels for GMLAN $1A data identifiers commonly used for module identification.</summary>
public static class GmIdentifiers
{
    public static readonly (byte Did, string Name)[] Gmlan =
    {
        (0x90, "VIN"),
        (0x92, "Supplier hardware number"),
        (0x97, "System name"),
        (0x98, "Repair shop code / tester serial"),
        (0x99, "Programming date"),
        (0xA0, "Manufacturer enable counter"),
        (0xB0, "Diagnostic address"),
        (0xB4, "Manufacturing traceability"),
        (0xC0, "Boot software part number"),
        (0xC1, "Software module 1 part number"),
        (0xC2, "Software module 2 part number"),
        (0xC3, "Software module 3 part number"),
        (0xC4, "Software module 4 part number"),
        (0xC5, "Software module 5 part number"),
        (0xC6, "Software module 6 part number"),
        (0xCB, "End model part number"),
        (0xCC, "Base model part number"),
    };

    public static string NameOf(byte did) =>
        Gmlan.FirstOrDefault(x => x.Did == did).Name ?? $"Identifier ${did:X2}";
}

public sealed record IdentItem(string Name, string Id, byte[] Raw)
{
    public string Hex => Core.Hex.Format(Raw);
    public string Value
    {
        get
        {
            var text = Core.Hex.AsText(Raw);
            if (text is { Length: >= 2 }) return text;
            if (Raw.Length == 4) return $"{Core.Hex.BigEndian(Raw)}";
            if (Raw.Length == 1) return $"${Raw[0]:X2}";
            return Hex;
        }
    }
}
