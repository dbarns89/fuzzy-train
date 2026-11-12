using System.Collections.ObjectModel;
using System.Windows.Input;
using VcxDiag.Core;
using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;

namespace VcxDiag.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

/// <summary>Vehicle overview: VIN, emissions status, calibration IDs, and the one-click health check.</summary>
public sealed class VehicleViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;

    public ObservableCollection<InfoRow> Identity { get; } = new();
    public ObservableCollection<InfoRow> Calibrations { get; } = new();
    public ObservableCollection<string> Summary { get; } = new();

    private string _milText = "Not read yet";
    public string MilText { get => _milText; set => Set(ref _milText, value); }

    private bool _milOn;
    public bool MilOn { get => _milOn; set => Set(ref _milOn, value); }

    public ICommand RefreshCommand { get; }
    public ICommand HealthCheckCommand { get; }

    public VehicleViewModel(Shell shell)
    {
        _shell = shell;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _shell.HasBus);
        HealthCheckCommand = new AsyncCommand(HealthCheckAsync, () => _shell.HasBus);
    }

    public async Task RefreshAsync()
    {
        var data = await _shell.Run("Read vehicle information", s =>
        {
            var vin = s.ReadVin();
            var info = Safe(() => s.ReadObdInfo()) ?? new List<ObdInfo>();
            var ready = Safe(() => s.ReadReadiness());
            return (vin, info, ready);
        });
        if (data == default) return;
        var (vin, infos, ready) = data;

        _shell.Vin = vin;
        Identity.Clear();
        if (vin is not null)
        {
            Identity.Add(new("VIN", vin));
            Identity.Add(new("Make (from VIN)", Vin.Maker(vin)));
            if (Vin.ModelYear(vin) is int y) Identity.Add(new("Model year (from VIN)", y.ToString()));
            Identity.Add(new("VIN check digit", Vin.IsValid(vin) ? "Valid" : "Does not match (VIN may be misread or non-North American)"));
        }
        else Identity.Add(new("VIN", "Not reported"));
        Identity.Add(new("Network", _shell.BusText));

        Calibrations.Clear();
        foreach (var i in infos)
        {
            if (i.EcuName is not null) Calibrations.Add(new($"{i.Source}: name", i.EcuName));
            foreach (var c in i.CalibrationIds) Calibrations.Add(new($"{i.Source}: calibration ID", c));
            foreach (var c in i.Cvns) Calibrations.Add(new($"{i.Source}: CVN", c));
        }
        if (Calibrations.Count == 0) Calibrations.Add(new("Calibration IDs", "Not reported (mode $09 not supported)"));

        if (ready is not null)
        {
            MilOn = ready.MilOn;
            MilText = $"Check engine light {(ready.MilOn ? "ON" : "off")}  •  {ready.DtcCount} emissions code(s)  •  " +
                      $"{ready.Monitors.Count(m => m.Supported && !m.Complete)} monitor(s) not complete";
        }
        else MilText = "Emissions status not reported";
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }

    /// <summary>Scan every module and read all codes in one go, like a quick full-vehicle report.</summary>
    private async Task HealthCheckAsync()
    {
        Summary.Clear();
        await RefreshAsync();
        var progress = new Progress<string>(s => _shell.Status = s);
        var result = await _shell.Run("Health check", s =>
        {
            var mods = s.ScanModules(progress);
            var codes = new List<Dtc>();
            foreach (var m in mods)
            {
                ((IProgress<string>)progress).Report($"Reading codes from {m.Name}…");
                try { codes.AddRange(s.ReadModuleDtcs(m)); } catch { /* module may not support it */ }
            }
            try
            {
                foreach (var d in s.ReadObdDtcs(DtcSource.Stored))
                    if (!codes.Any(c => c.Code == d.Code)) codes.Add(d);
                foreach (var d in s.ReadObdDtcs(DtcSource.Pending))
                    if (!codes.Any(c => c.Code == d.Code)) codes.Add(d);
            }
            catch { }
            return (mods, codes);
        });
        if (result == default) return;
        _shell.Modules.Clear();
        foreach (var m in result.mods) _shell.Modules.Add(m);
        _shell.Codes.Clear();
        foreach (var c in result.codes) _shell.Codes.Add(c);

        Summary.Add($"{result.mods.Count} module(s) answered.");
        foreach (var m in result.mods)
            Summary.Add($"{m.Name} [{m.AddressText}]: {(m.DtcCount is int n ? n == 0 ? "no codes" : $"{n} code(s)" : "codes not readable")}");
        var obd = result.codes.Count(c => c.Source != DtcSource.Module);
        if (obd > 0) Summary.Add($"{obd} additional emissions code(s) from OBD.");
        Summary.Add(result.codes.Count == 0 ? "No trouble codes found." : $"Total: {result.codes.Count} code(s). See Trouble Codes for details.");
        _shell.Status = $"Health check done: {result.mods.Count} modules, {result.codes.Count} codes.";
    }
}
