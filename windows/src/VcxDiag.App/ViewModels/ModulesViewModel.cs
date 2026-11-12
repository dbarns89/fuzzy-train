using System.Collections.ObjectModel;
using System.Windows.Input;
using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;

namespace VcxDiag.App.ViewModels;

public sealed class ModulesViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;
    public ObservableCollection<EcuModule> Modules => _shell.Modules;
    public ObservableCollection<IdentItem> Identification { get; } = new();
    public ObservableCollection<Dtc> ModuleCodes { get; } = new();

    private EcuModule? _selected;
    public EcuModule? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Identification.Clear();
            ModuleCodes.Clear();
            Raise(nameof(SelectedTitle));
        }
    }

    public string SelectedTitle => _selected is null ? "Select a module on the left" : $"{_selected.Name}  [{_selected.AddressText}]";

    public ICommand ScanCommand { get; }
    public ICommand IdentifyCommand { get; }
    public ICommand ReadCodesCommand { get; }
    public ICommand ClearCodesCommand { get; }

    public ModulesViewModel(Shell shell)
    {
        _shell = shell;
        ScanCommand = new AsyncCommand(ScanAsync, () => _shell.HasBus);
        IdentifyCommand = new AsyncCommand(IdentifyAsync, () => _shell.HasBus && Selected is not null);
        ReadCodesCommand = new AsyncCommand(ReadCodesAsync, () => _shell.HasBus && Selected is not null);
        ClearCodesCommand = new AsyncCommand(ClearCodesAsync, () => _shell.HasBus && Selected is not null);
    }

    private async Task ScanAsync()
    {
        var progress = new Progress<string>(s => _shell.Status = s);
        var mods = await _shell.Run("Module scan", s => s.ScanModules(progress));
        if (mods is null) return;
        _shell.Modules.Clear();
        foreach (var m in mods) _shell.Modules.Add(m);
        _shell.Status = mods.Count == 0
            ? "No modules answered. Check ignition is ON and the right network is selected."
            : $"Found {mods.Count} module(s).";
        if (mods.Count > 0) Selected = mods[0];
    }

    private async Task IdentifyAsync()
    {
        var m = Selected!;
        var items = await _shell.Run($"Read identification from {m.Name}", s => s.ReadIdentification(m));
        Identification.Clear();
        if (items is null) return;
        foreach (var i in items) Identification.Add(i);
        if (items.Count == 0) _shell.Status = "The module did not report identification data.";
    }

    private async Task ReadCodesAsync()
    {
        var m = Selected!;
        var codes = await _shell.Run($"Read codes from {m.Name}", s => s.ReadModuleDtcs(m));
        ModuleCodes.Clear();
        if (codes is null) return;
        foreach (var c in codes) ModuleCodes.Add(c);
        _shell.Status = codes.Count == 0 ? $"{m.Name}: no codes." : $"{m.Name}: {codes.Count} code(s).";
        RefreshRow(m);
    }

    private async Task ClearCodesAsync()
    {
        var m = Selected!;
        if (!Dialogs.Confirm($"Clear all trouble codes in {m.Name}?\n\nThis also erases freeze frame data and resets emissions monitors if it's the engine module. Ignition ON, engine OFF is recommended.", "Clear codes"))
            return;
        var ok = await _shell.Run($"Clear codes in {m.Name}", s => s.ClearModuleDtcs(m));
        _shell.Status = ok ? $"{m.Name}: codes cleared." : $"{m.Name}: the module did not confirm the clear.";
        _shell.Write(_shell.Status);
        ModuleCodes.Clear();
        RefreshRow(m);
    }

    private void RefreshRow(EcuModule m)
    {
        // EcuModule isn't observable; re-insert it so the grid shows the new code count.
        var i = Modules.IndexOf(m);
        if (i < 0) return;
        Modules.RemoveAt(i);
        Modules.Insert(i, m);
        _selected = m;
        Raise(nameof(Selected));
    }
}
