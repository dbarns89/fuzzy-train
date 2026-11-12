using System.Collections.ObjectModel;
using System.Windows.Input;
using VcxDiag.Core.Obd;

namespace VcxDiag.App.ViewModels;

public sealed record ValueRow(string Name, string Value, string Unit);

/// <summary>Freeze frame, readiness monitors and mode $06 test results.</summary>
public sealed class EmissionsViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;

    public ObservableCollection<ValueRow> FreezeFrame { get; } = new();
    public ObservableCollection<MonitorState> Monitors { get; } = new();
    public ObservableCollection<TestResult> Tests { get; } = new();

    private string _freezeTitle = "Freeze frame: not read yet";
    public string FreezeTitle { get => _freezeTitle; set => Set(ref _freezeTitle, value); }

    private string _readinessTitle = "Readiness: not read yet";
    public string ReadinessTitle { get => _readinessTitle; set => Set(ref _readinessTitle, value); }

    private string _testsTitle = "On-board test results: not read yet";
    public string TestsTitle { get => _testsTitle; set => Set(ref _testsTitle, value); }

    public ICommand ReadFreezeCommand { get; }
    public ICommand ReadReadinessCommand { get; }
    public ICommand ReadTestsCommand { get; }

    public EmissionsViewModel(Shell shell)
    {
        _shell = shell;
        ReadFreezeCommand = new AsyncCommand(ReadFreezeAsync, () => _shell.HasBus);
        ReadReadinessCommand = new AsyncCommand(ReadReadinessAsync, () => _shell.HasBus);
        ReadTestsCommand = new AsyncCommand(ReadTestsAsync, () => _shell.HasBus);
    }

    private async Task ReadFreezeAsync()
    {
        var ff = await _shell.Run("Read freeze frame", s => (object?)s.ReadFreezeFrame() ?? "none");
        FreezeFrame.Clear();
        if (ff is not VcxDiag.Core.FreezeFrame f) { FreezeTitle = ff is null ? FreezeTitle : "No freeze frame stored."; return; }
        FreezeTitle = $"Freeze frame captured for {f.TriggerCode}: {DtcDescriptions.Describe(f.TriggerCode ?? "")}";
        bool us = _shell.Settings.UsUnits;
        foreach (var (p, v) in f.Values)
        {
            var (val, unit) = us ? Units.ToImperial(v, p.Unit, p.Kind) : (v, p.Unit);
            FreezeFrame.Add(new ValueRow(p.Name, double.IsNaN(val) ? "n/a" : LiveParameter.Format(val), unit));
        }
    }

    private async Task ReadReadinessAsync()
    {
        var r = await _shell.Run("Read readiness monitors", s => (object?)s.ReadReadiness() ?? "none");
        Monitors.Clear();
        if (r is not ReadinessStatus st) { if (r is not null) ReadinessTitle = "The engine module did not report readiness."; return; }
        foreach (var m in st.Monitors) Monitors.Add(m);
        var incomplete = st.Monitors.Count(m => m.Supported && !m.Complete);
        ReadinessTitle = $"Check engine light {(st.MilOn ? "ON" : "off")}  •  {st.DtcCount} emissions code(s)  •  " +
                         (incomplete == 0 ? "all supported monitors complete" : $"{incomplete} monitor(s) not complete") +
                         (st.Diesel ? "  •  diesel" : "");
    }

    private async Task ReadTestsAsync()
    {
        var list = await _shell.Run("Read on-board test results", s => s.ReadMode06());
        Tests.Clear();
        if (list is null) return;
        foreach (var t in list) Tests.Add(t);
        var fails = list.Count(t => !t.Passed);
        TestsTitle = list.Count == 0 ? "No test results reported." : $"{list.Count} result(s), {fails} outside limits.";
    }
}
