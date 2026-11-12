namespace VcxDiag.Core.Gm;

/// <summary>Basic VIN decoding from public ISO 3779 / NHTSA rules.</summary>
public static class Vin
{
    private const string Weights = "8765432X098765432";

    private static int Value(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1, 'B' or 'K' or 'S' => 2, 'C' or 'L' or 'T' => 3, 'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5, 'F' or 'W' => 6, 'G' or 'P' or 'X' => 7, 'H' or 'Y' => 8, 'R' or 'Z' => 9,
        _ => -1,
    };

    public static char? CheckDigit(string vin)
    {
        if (vin.Length != 17) return null;
        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            if (i == 8) continue;
            int v = Value(char.ToUpperInvariant(vin[i]));
            if (v < 0) return null;
            int w = Weights[i] == 'X' ? 10 : Weights[i] - '0';
            sum += v * w;
        }
        int r = sum % 11;
        return r == 10 ? 'X' : (char)('0' + r);
    }

    public static bool IsValid(string vin) => vin.Length == 17 && CheckDigit(vin) == char.ToUpperInvariant(vin[8]);

    public static int? ModelYear(string vin)
    {
        if (vin.Length != 17) return null;
        const string codes = "ABCDEFGHJKLMNPRSTVWXY123456789";
        int idx = codes.IndexOf(char.ToUpperInvariant(vin[9]));
        if (idx < 0) return null;
        int year = 1980 + idx;
        // North American VINs: a letter in position 7 means the 2010+ cycle.
        if (char.IsLetter(vin[6]) && year + 30 <= DateTime.Now.Year + 1) year += 30;
        return year;
    }

    private static readonly Dictionary<string, string> Wmi = new()
    {
        ["1G1"] = "Chevrolet (car)", ["1GC"] = "Chevrolet (truck)", ["1GN"] = "Chevrolet (SUV/van)",
        ["1GB"] = "Chevrolet (incomplete/commercial)", ["1GT"] = "GMC (truck)", ["1GK"] = "GMC (SUV/van)",
        ["1GD"] = "GMC (incomplete/commercial)", ["1G4"] = "Buick", ["1G6"] = "Cadillac", ["1GY"] = "Cadillac (SUV)",
        ["1G2"] = "Pontiac", ["1GM"] = "Pontiac (van)", ["1G3"] = "Oldsmobile", ["1G8"] = "Saturn",
        ["2G1"] = "Chevrolet (Canada)", ["2G2"] = "Pontiac (Canada)", ["2G4"] = "Buick (Canada)",
        ["2GC"] = "Chevrolet truck (Canada)", ["2GT"] = "GMC truck (Canada)", ["2GN"] = "Chevrolet SUV (Canada)",
        ["2GK"] = "GMC SUV (Canada)", ["3G1"] = "Chevrolet (Mexico)", ["3GN"] = "Chevrolet SUV (Mexico)",
        ["3GC"] = "Chevrolet truck (Mexico)", ["3GT"] = "GMC truck (Mexico)", ["3GK"] = "GMC SUV (Mexico)",
        ["3G7"] = "Pontiac (Mexico)", ["5GA"] = "Buick (SUV)", ["5GZ"] = "Saturn (SUV)", ["5GR"] = "Hummer",
        ["5GT"] = "Hummer", ["KL1"] = "Chevrolet (Korea)", ["KL4"] = "Buick (Korea)", ["KL7"] = "Chevrolet (Korea)",
        ["W0L"] = "Opel / Vauxhall", ["YS3"] = "Saab", ["6G1"] = "Holden / Chevrolet (Australia)",
    };

    public static string Maker(string vin) =>
        vin.Length >= 3 && Wmi.TryGetValue(vin[..3].ToUpperInvariant(), out var m) ? m : "Unknown manufacturer code " + (vin.Length >= 3 ? vin[..3] : vin);

    /// <summary>Extracts a VIN-looking string from raw bytes (ignores padding and counters).</summary>
    public static string? FromBytes(IEnumerable<byte> data)
    {
        var chars = data.Where(b => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z')
            .Select(b => (char)b).ToArray();
        if (chars.Length < 17) return null;
        return new string(chars[^17..]);
    }
}
