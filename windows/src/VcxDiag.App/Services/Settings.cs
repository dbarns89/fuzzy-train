using System.Text.Json;
using VcxDiag.Core;

namespace VcxDiag.App;

public static class Paths
{
    /// <summary>Documents\VCX Diag: logs, recordings, reports and user files live here.</summary>
    public static string Documents
    {
        get
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VCX Diag");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string Logs => Sub("Logs");
    public static string Recordings => Sub("Recordings");
    public static string Reports => Sub("Reports");

    private static string Sub(string name)
    {
        var p = Path.Combine(Documents, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string SettingsFile => Path.Combine(Documents, "settings.json");
    public static string FunctionsFile => Path.Combine(Documents, "special-functions.json");
    public static string CustomPidsFile => Path.Combine(Documents, "custom-parameters.json");
    public static string DtcFile => Path.Combine(Documents, "dtc-descriptions.csv");
}

public sealed class Settings
{
    public bool UsUnits { get; set; } = true;
    public int PollDelayMs { get; set; } = 50;
    public bool LogBusTraffic { get; set; }
    public string? LastDriverDll { get; set; }
    public int LastBus { get; set; } = -1;
    public List<string> LastLivePids { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new Settings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}

/// <summary>A saved request the user can run from the Functions page (with confirmation).</summary>
public sealed class SpecialFunction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"OBD", "ALL" (GM functional) or a module address in hex, e.g. "7E0" or "40".</summary>
    public string Target { get; set; } = "OBD";
    /// <summary>Request bytes in hex, e.g. "01 0C".</summary>
    public string Request { get; set; } = "";
    /// <summary>Optional request sent afterwards to end the function (e.g. return to normal).</summary>
    public string? StopRequest { get; set; }
    public bool ChangesVehicle { get; set; } = true;
}

public static class UserFiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static List<SpecialFunction> LoadFunctions()
    {
        if (!File.Exists(Paths.FunctionsFile))
        {
            var sample = new List<SpecialFunction>
            {
                new()
                {
                    Name = "Example: read engine speed (safe)",
                    Description = "Sample entry showing the file format. Add your own requests from service information you are entitled to.",
                    Target = "OBD", Request = "01 0C", ChangesVehicle = false,
                },
            };
            try { File.WriteAllText(Paths.FunctionsFile, JsonSerializer.Serialize(sample, Json)); } catch { }
            return sample;
        }
        return JsonSerializer.Deserialize<List<SpecialFunction>>(File.ReadAllText(Paths.FunctionsFile), Json) ?? new();
    }

    public static List<CustomParameter> LoadCustomParameters()
    {
        if (!File.Exists(Paths.CustomPidsFile))
        {
            var sample = new List<CustomParameter>
            {
                new() { Name = "Demo counter (simulator only)", Module = 0x7E0, Identifier = 0x1234, Length = 2, Scale = 0.1, Unit = "s" },
            };
            try { File.WriteAllText(Paths.CustomPidsFile, JsonSerializer.Serialize(sample, Json)); } catch { }
            return sample;
        }
        return JsonSerializer.Deserialize<List<CustomParameter>>(File.ReadAllText(Paths.CustomPidsFile), Json) ?? new();
    }
}
