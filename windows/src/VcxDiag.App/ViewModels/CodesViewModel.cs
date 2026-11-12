using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using VcxDiag.Core.Obd;

namespace VcxDiag.App.ViewModels;

public sealed class CodesViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;
    public ObservableCollection<Dtc> Codes => _shell.Codes;

    private Dtc? _selected;
    public Dtc? Selected { get => _selected; set => Set(ref _selected, value); }

    public ICommand ReadAllCommand { get; }
    public ICommand ReadStoredCommand { get; }
    public ICommand ReadPendingCommand { get; }
    public ICommand ReadPermanentCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CopyCommand { get; }

    public CodesViewModel(Shell shell)
    {
        _shell = shell;
        ReadAllCommand = new AsyncCommand(ReadAllAsync, () => _shell.HasBus);
        ReadStoredCommand = new AsyncCommand(() => ReadObdAsync(DtcSource.Stored), () => _shell.HasBus);
        ReadPendingCommand = new AsyncCommand(() => ReadObdAsync(DtcSource.Pending), () => _shell.HasBus);
        ReadPermanentCommand = new AsyncCommand(() => ReadObdAsync(DtcSource.Permanent), () => _shell.HasBus);
        ClearAllCommand = new AsyncCommand(ClearAllAsync, () => _shell.HasBus);
        ExportCommand = new RelayCommand(Export, () => Codes.Count > 0);
        CopyCommand = new RelayCommand(Copy, () => Codes.Count > 0);
    }

    /// <summary>Reads GM codes from every module (scanning first if needed) plus OBD codes.</summary>
    private async Task ReadAllAsync()
    {
        var known = _shell.Modules.ToList();
        var progress = new Progress<string>(s => _shell.Status = s);
        var result = await _shell.Run("Read codes from all modules", s =>
        {
            var mods = known.Count > 0 ? known : s.ScanModules(progress);
            var list = new List<Dtc>();
            foreach (var m in mods)
            {
                ((IProgress<string>)progress).Report($"Reading {m.Name}…");
                try { list.AddRange(s.ReadModuleDtcs(m)); }
                catch (Exception e) { _shell.Write($"{m.Name}: {e.Message}"); }
            }
            foreach (var d in s.ReadObdDtcs(DtcSource.Stored))
                if (!list.Any(c => c.Code == d.Code)) list.Add(d);
            return (mods, list);
        });
        if (result == default) return;
        if (known.Count == 0) foreach (var m in result.mods) _shell.Modules.Add(m);
        Show(result.list);
    }

    private async Task ReadObdAsync(DtcSource which)
    {
        var list = await _shell.Run($"Read {which.ToString().ToLowerInvariant()} emissions codes", s => s.ReadObdDtcs(which));
        if (list is not null) Show(list);
    }

    private void Show(List<Dtc> list)
    {
        Codes.Clear();
        foreach (var d in list) Codes.Add(d);
        _shell.Status = list.Count == 0 ? "No trouble codes found." : $"{list.Count} trouble code(s).";
        foreach (var d in list) _shell.Write($"DTC {d.FullCode} [{d.Module}] {d.Description} {d.StatusText}");
    }

    private async Task ClearAllAsync()
    {
        if (!Dialogs.Confirm(
                "Clear trouble codes in ALL modules?\n\n" +
                "• Freeze frame data and emissions readiness will be erased.\n" +
                "• The vehicle may need a drive cycle before passing an emissions test.\n" +
                "• Do this with the ignition ON and the engine OFF.\n\nWrite the codes down or save a report first.",
                "Clear all codes"))
            return;
        var n = await _shell.Run("Clear all codes", s => s.ClearAllDtcs());
        _shell.Write($"Clear all codes: {n} module(s) confirmed");
        Codes.Clear();
        _shell.Status = n > 0 ? $"Codes cleared ({n} module(s) confirmed). Read again to verify." : "No module confirmed the clear. Read codes again to check.";
    }

    private string ToCsv()
    {
        var sb = new StringBuilder("Module,Address,Code,Description,Failure type,Status,Source\r\n");
        foreach (var d in Codes)
            sb.Append($"\"{d.Module}\",{d.ModuleAddress:X},{d.FullCode},\"{d.Description}\",\"{d.FailureTypeText}\",\"{d.StatusText}\",{d.Source}\r\n");
        return sb.ToString();
    }

    private void Export()
    {
        var path = Dialogs.SaveFile("CSV file (*.csv)|*.csv", $"codes-{_shell.Vin ?? "vehicle"}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (path is null) return;
        File.WriteAllText(path, ToCsv());
        _shell.Status = "Saved " + path;
    }

    private void Copy()
    {
        Clipboard.SetText(string.Join(Environment.NewLine, Codes.Select(d => $"{d.FullCode}\t{d.Module}\t{d.Description}\t{d.StatusText}")));
        _shell.Status = "Codes copied to the clipboard.";
    }
}
