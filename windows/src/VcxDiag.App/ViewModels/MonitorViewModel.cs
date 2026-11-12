using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using VcxDiag.Core;

namespace VcxDiag.App.ViewModels;

public sealed record BusFrameRow(string Time, string Header, string Data, string Text);

/// <summary>Raw bus monitor: shows every frame on the selected network.</summary>
public sealed class MonitorViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;
    public ObservableCollection<BusFrameRow> Frames { get; } = new();

    private bool _running;
    public bool Running { get => _running; set { Set(ref _running, value); Raise(nameof(StartStopText)); } }
    public string StartStopText => Running ? "Stop" : "Start";

    private bool _paused;
    public bool Paused { get => _paused; set => Set(ref _paused, value); }

    private string _filter = "";
    /// <summary>Only show frames whose header / ID starts with this text.</summary>
    public string Filter { get => _filter; set => Set(ref _filter, value); }

    private string _counter = "";
    public string Counter { get => _counter; set => Set(ref _counter, value); }

    public ICommand StartStopCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SaveCommand { get; }

    private CancellationTokenSource? _cts;
    private readonly List<string> _all = new();

    public MonitorViewModel(Shell shell)
    {
        _shell = shell;
        StartStopCommand = new AsyncCommand(() => Running ? StopAsync() : StartAsync(), () => _shell.HasBus || Running);
        ClearCommand = new RelayCommand(() => { Frames.Clear(); _all.Clear(); Counter = ""; });
        SaveCommand = new RelayCommand(Save, () => _all.Count > 0);
        _shell.StopBackgroundWork += () => { if (Running) _ = StopAsync(); };
    }

    private async Task StartAsync()
    {
        var s = _shell.Session;
        if (s?.Bus is null) return;
        _shell.StopBackground(); // stops live data
        await _shell.Worker.Run(() => s.Bus!.BeginMonitor());
        _shell.MonitorActive = true;
        _cts = new CancellationTokenSource();
        Running = true;
        _shell.Status = "Bus monitor running. Other functions are paused until you stop it.";
        _ = Loop(s, _cts.Token);
    }

    private async Task StopAsync()
    {
        _cts?.Cancel();
        var s = _shell.Session;
        if (s?.Bus is not null) await _shell.Worker.Run(() => s.Bus!.EndMonitor());
        _shell.MonitorActive = false;
        Running = false;
        _shell.Status = "Bus monitor stopped.";
    }

    private async Task Loop(DiagnosticSession s, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long count = 0;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<VcxDiag.Core.Transport.RawFrame> frames;
            try { frames = await _shell.Worker.Run(() => s.Bus!.ReadRaw(50)); }
            catch (Exception e) { _shell.Write("Monitor: " + e.Message); break; }
            foreach (var f in frames)
            {
                count++;
                var (hdr, data) = s.Bus!.SplitFrame(f.Data);
                var t = (clock.Elapsed.TotalMilliseconds / 1000).ToString("0.000");
                var line = $"{t}\t{hdr}\t{Hex.Format(data)}";
                _all.Add(line);
                if (_all.Count > 200_000) _all.RemoveRange(0, 50_000);
                if (Paused || (_filter.Length > 0 && !hdr.Replace(" ", "").StartsWith(_filter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                    continue;
                Frames.Add(new BusFrameRow(t, hdr, Hex.Format(data), Printable(data)));
            }
            while (Frames.Count > 3000) Frames.RemoveAt(0);
            Counter = $"{count} frame(s)";
        }
    }

    private static string Printable(byte[] d)
    {
        var sb = new StringBuilder(d.Length);
        foreach (var b in d) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.ToString();
    }

    private void Save()
    {
        var path = Dialogs.SaveFile("Text log (*.txt)|*.txt", $"bus-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (path is null) return;
        File.WriteAllLines(path, new[] { "time_s\theader\tdata" }.Concat(_all));
        _shell.Status = "Saved " + path;
    }
}
