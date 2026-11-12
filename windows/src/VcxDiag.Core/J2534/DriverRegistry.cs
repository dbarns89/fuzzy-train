using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VcxDiag.Core.J2534;

public sealed record J2534Driver(string Name, string Vendor, string DllPath, bool Is64Bit, IReadOnlyList<string> Protocols)
{
    public bool LooksLikeVcx =>
        (Name + " " + Vendor + " " + DllPath).Contains("vx", StringComparison.OrdinalIgnoreCase) ||
        (Name + " " + Vendor + " " + DllPath).Contains("allscanner", StringComparison.OrdinalIgnoreCase);

    public bool MatchesProcess => Is64Bit == Environment.Is64BitProcess;

    public override string ToString() => $"{Name} ({(Is64Bit ? "64" : "32")}-bit)";
}

/// <summary>Finds installed J2534 drivers. They register under HKLM\SOFTWARE\PassThruSupport.04.04.</summary>
public static class DriverRegistry
{
    private static readonly string[] ProtocolValues =
        { "J1850VPW", "J1850PWM", "ISO9141", "ISO14230", "CAN", "ISO15765", "SW_CAN_PS", "SW_ISO15765_PS", "CAN_PS" };

    public static IReadOnlyList<J2534Driver> Find()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<J2534Driver>();
        return FindWindows();
    }

    [SupportedOSPlatform("windows")]
    private static List<J2534Driver> FindWindows()
    {
        var found = new List<J2534Driver>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hklm.OpenSubKey(@"SOFTWARE\PassThruSupport.04.04");
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("FunctionLibrary") is not string dll || !seen.Add(dll)) continue;
                    var protos = ProtocolValues.Where(p => k.GetValue(p) is int v && v != 0).ToList();
                    found.Add(new J2534Driver(
                        k.GetValue("Name") as string ?? sub,
                        k.GetValue("Vendor") as string ?? "",
                        dll,
                        PeInfo.Is64Bit(dll) ?? view == RegistryView.Registry64,
                        protos));
                }
            }
            catch (Exception) { /* unreadable key: skip */ }
        }
        // VCX drivers first, then drivers that match our bitness.
        return found.OrderByDescending(d => d.LooksLikeVcx).ThenByDescending(d => d.MatchesProcess).ToList();
    }
}

public static class PeInfo
{
    /// <summary>Reads the PE header to tell whether a DLL is 64-bit. Null if it can't be read.</summary>
    public static bool? Is64Bit(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            var machine = br.ReadUInt16();
            return machine is 0x8664 or 0xAA64;
        }
        catch { return null; }
    }
}
