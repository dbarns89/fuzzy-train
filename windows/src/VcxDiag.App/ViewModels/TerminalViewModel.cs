using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using VcxDiag.Core;
using VcxDiag.Core.Gm;

namespace VcxDiag.App.ViewModels;

public sealed record TargetOption(string Title, uint? Address, bool Obd)
{
    public override string ToString() => Title;
}

/// <summary>Expert terminal (send any request) plus built-in and user-defined special functions.</summary>
public sealed class TerminalViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;

    public ObservableCollection<TargetOption> Targets { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public ObservableCollection<SpecialFunction> Functions { get; } = new();

    private TargetOption? _target;
    public TargetOption? Target { get => _target; set => Set(ref _target, value); }

    private string _customAddress = "";
    /// <summary>Overrides the target with a typed address (hex), e.g. 7E0 or 40.</summary>
    public string CustomAddress { get => _customAddress; set => Set(ref _customAddress, value); }

    private string _request = "01 00";
    public string Request { get => _request; set => Set(ref _request, value); }

    private SpecialFunction? _selectedFunction;
    public SpecialFunction? SelectedFunction { get => _selectedFunction; set => Set(ref _selectedFunction, value); }

    public ICommand SendCommand { get; }
    public ICommand RunFunctionCommand { get; }
    public ICommand StopFunctionCommand { get; }
    public ICommand ReloadFunctionsCommand { get; }
    public ICommand EditFunctionsCommand { get; }
    public ICommand ReturnToNormalCommand { get; }
    public ICommand TesterPresentCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public TerminalViewModel(Shell shell)
    {
        _shell = shell;
        SendCommand = new AsyncCommand(SendAsync, () => _shell.HasBus);
        RunFunctionCommand = new AsyncCommand(RunFunctionAsync, () => _shell.HasBus && SelectedFunction is not null);
        StopFunctionCommand = new AsyncCommand(StopFunctionAsync, () => _shell.HasBus && SelectedFunction?.StopRequest is { Length: > 0 });
        ReloadFunctionsCommand = new RelayCommand(LoadFunctions);
        EditFunctionsCommand = new RelayCommand(() => LiveDataViewModel.OpenInEditor(Paths.FunctionsFile));
        ReturnToNormalCommand = new AsyncCommand(ReturnToNormalAsync, () => _shell.HasBus);
        TesterPresentCommand = new AsyncCommand(async () =>
        {
            if (await _shell.Run("Tester present", s => s.TesterPresent())) Echo("→ tester present sent (no reply expected)");
        }, () => _shell.HasBus);
        ClearHistoryCommand = new RelayCommand(() => History.Clear());
        _shell.Modules.CollectionChanged += (_, _) => RebuildTargets();
        RebuildTargets();
        LoadFunctions();
    }

    public void RebuildTargets()
    {
        var keep = Target?.Title;
        Targets.Clear();
        Targets.Add(new TargetOption("OBD-II functional (all emissions modules)", null, true));
        Targets.Add(new TargetOption("GM functional (all modules)", null, false));
        foreach (var m in _shell.Modules) Targets.Add(new TargetOption($"{m.Name} [{m.AddressText}]", m.Address, false));
        Target = Targets.FirstOrDefault(t => t.Title == keep) ?? Targets[0];
    }

    private void LoadFunctions()
    {
        Functions.Clear();
        try { foreach (var f in UserFiles.LoadFunctions()) Functions.Add(f); }
        catch (Exception e) { Dialogs.Error("special-functions.json could not be read:\n" + e.Message); }
    }

    private void Echo(string s)
    {
        History.Add(s);
        while (History.Count > 500) History.RemoveAt(0);
    }

    private (uint? Address, bool Obd) ResolveTarget(string? overrideText = null)
    {
        var text = (overrideText ?? CustomAddress).Trim().Replace("$", "").Replace("0x", "");
        if (text.Length > 0)
        {
            if (text.Equals("OBD", StringComparison.OrdinalIgnoreCase)) return (null, true);
            if (text.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return (null, false);
            return (uint.Parse(text, NumberStyles.HexNumber), false);
        }
        return (Target?.Address, Target?.Obd ?? true);
    }

    private async Task SendAsync()
    {
        byte[] payload;
        (uint? addr, bool obd) t;
        try
        {
            payload = Hex.Parse(Request);
            t = ResolveTarget();
        }
        catch (Exception e) { Dialogs.Error("Check the request: " + e.Message); return; }
        if (payload.Length == 0) return;
        await Exchange(t.addr, t.obd, payload);
    }

    private async Task Exchange(uint? addr, bool obd, byte[] payload)
    {
        var bus = _shell.Session?.Bus;
        string dest = obd ? "OBD" : addr is uint a ? bus?.FormatAddress(a) ?? a.ToString("X") : "ALL";
        Echo($"→ [{dest}] {Hex.Format(payload)}");
        var replies = await _shell.Run("Send request", s => s.Raw(addr, obd, payload), quiet: true);
        if (replies is null) { Echo("   (error, see status bar)"); return; }
        if (replies.Count == 0) Echo("   no reply");
        foreach (var r in replies)
        {
            var src = bus?.FormatAddress(r.Source) ?? r.Source.ToString("X");
            var note = r.IsNegative ? $"   negative response, code ${r.NegativeCode:X2} ({NegativeText(r.NegativeCode)})" : "";
            var text = Hex.AsText(r.Payload.Length > 2 ? r.Payload[2..] : r.Payload);
            Echo($"← [{src}] {Hex.Format(r.Payload)}{(r.Unacknowledged ? "  (UUDT)" : "")}{note}{(text is { Length: > 3 } ? $"   \"{text}\"" : "")}");
        }
    }

    public static string NegativeText(byte code) => code switch
    {
        0x10 => "general reject", 0x11 => "service not supported", 0x12 => "sub-function not supported / invalid format",
        0x21 => "busy, repeat request", 0x22 => "conditions not correct", 0x31 => "request out of range",
        0x33 => "security access denied", 0x35 => "invalid key", 0x36 => "exceeded number of attempts",
        0x78 => "response pending", 0x7E => "not supported in active session", 0x7F => "service not supported in active session",
        0xE3 => "device control limits exceeded", _ => "see service documentation",
    };

    private async Task RunFunctionAsync()
    {
        var f = SelectedFunction!;
        byte[] payload;
        (uint? addr, bool obd) t;
        try
        {
            payload = Hex.Parse(f.Request);
            t = ResolveTarget(f.Target);
        }
        catch (Exception e) { Dialogs.Error($"\"{f.Name}\" is not set up correctly: {e.Message}"); return; }

        if (f.ChangesVehicle && !Dialogs.Confirm(
                $"Run \"{f.Name}\"?\n\n{f.Description}\n\nRequest: {Hex.Format(payload)} → {f.Target}\n\n" +
                "This can move parts or change how the vehicle behaves. Make sure the vehicle is parked, in Park/Neutral, " +
                "with the parking brake set, and that nobody is near moving parts.", "Special function"))
            return;
        _shell.Write($"Function: {f.Name}");
        await Exchange(t.addr, t.obd, payload);
    }

    private async Task StopFunctionAsync()
    {
        var f = SelectedFunction!;
        try { await Exchange(ResolveTarget(f.Target).Address, ResolveTarget(f.Target).Obd, Hex.Parse(f.StopRequest!)); }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    private async Task ReturnToNormalAsync()
    {
        var n = await _shell.Run("Return all modules to normal", s => s.ReturnToNormal());
        Echo($"→ return to normal mode sent, {n} module(s) answered");
    }
}
