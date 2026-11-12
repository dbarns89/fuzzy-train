using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VcxDiag.App.Controls;
using VcxDiag.Core;
using VcxDiag.Core.Obd;

namespace VcxDiag.App.ViewModels;

/// <summary>One row in the live data list.</summary>
public sealed class LiveParameter : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string MetricUnit { get; init; } = "";
    public UnitKind Kind { get; init; }
    public ObdParameter? Obd { get; init; }
    public CustomParameter? Custom { get; init; }
    public string Group => Custom is not null ? "Custom ($22)" : "OBD-II";

    private bool _selected;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) SelectionChanged?.Invoke(this); } }

    private bool _graphed;
    public bool IsGraphed { get => _graphed; set { if (Set(ref _graphed, value)) GraphChanged?.Invoke(this); } }

    public event Action<LiveParameter>? SelectionChanged;
    public event Action<LiveParameter>? GraphChanged;

    private string _valueText = "—", _minText = "", _maxText = "", _unit = "";
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string MinText { get => _minText; private set => Set(ref _minText, value); }
    public string MaxText { get => _maxText; private set => Set(ref _maxText, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }

    public double? Last { get; private set; }
    private double _min = double.MaxValue, _max = double.MinValue;
    public ChartSeries? Series { get; set; }

    public void Update(double metric, bool us)
    {
        var (v, unit) = us ? Units.ToImperial(metric, MetricUnit, Kind) : (metric, MetricUnit);
        Unit = unit;
        if (double.IsNaN(v)) { ValueText = "n/a"; return; }
        Last = v;
        _min = Math.Min(_min, v);
        _max = Math.Max(_max, v);
        ValueText = Format(v);
        MinText = Format(_min);
        MaxText = Format(_max);
        Series?.Add(v);
    }

    public void ResetStats()
    {
        _min = double.MaxValue; _max = double.MinValue;
        MinText = MaxText = "";
        Series?.Clear();
    }

    public void SetUnitLabel(bool us) => Unit = us ? Units.ToImperial(0, MetricUnit, Kind).Unit : MetricUnit;

    public static string Format(double v) => Math.Abs(v) switch
    {
        >= 1000 => v.ToString("0", CultureInfo.CurrentCulture),
        >= 100 => v.ToString("0.0", CultureInfo.CurrentCulture),
        >= 1 => v.ToString("0.0#", CultureInfo.CurrentCulture),
        _ => v.ToString("0.###", CultureInfo.CurrentCulture),
    };
}

public sealed class LiveDataViewModel : ObservableObject
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x2F, 0x80, 0xED), Color.FromRgb(0xF2, 0x99, 0x4A), Color.FromRgb(0x27, 0xAE, 0x60),
        Color.FromRgb(0xEB, 0x57, 0x57), Color.FromRgb(0x9B, 0x51, 0xE0), Color.FromRgb(0x00, 0xA3, 0xA3),
    };

    private readonly Shell _shell;
    public Shell Shell => _shell;

    public ObservableCollection<LiveParameter> All { get; } = new();
    public ICollectionView Available { get; }
    public ObservableCollection<LiveParameter> Watching { get; } = new();
    public ObservableCollection<ChartSeries> Series { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Available.Refresh(); } }

    private bool _onlySupported;
    public bool OnlySupported { get => _onlySupported; set { if (Set(ref _onlySupported, value)) Available.Refresh(); } }

    private HashSet<byte> _supported = new();

    private bool _running;
    public bool Running { get => _running; set { Set(ref _running, value); Raise(nameof(StartStopText)); } }
    public string StartStopText => Running ? "Stop" : "Start";

    private string _rate = "";
    public string Rate { get => _rate; set => Set(ref _rate, value); }

    private int _chartVersion;
    public int ChartVersion { get => _chartVersion; set => Set(ref _chartVersion, value); }

    private StreamWriter? _csv;
    private List<LiveParameter> _csvColumns = new();
    private readonly Stopwatch _csvClock = new();
    public bool Recording => _csv is not null;
    public string RecordText => Recording ? "Stop recording" : "Record to CSV";

    private string _recordInfo = "";
    public string RecordInfo { get => _recordInfo; set => Set(ref _recordInfo, value); }

    public ICommand StartStopCommand { get; }
    public ICommand LoadSupportedCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ReloadCustomCommand { get; }
    public ICommand OpenCustomFileCommand { get; }

    private CancellationTokenSource? _cts;

    public LiveDataViewModel(Shell shell)
    {
        _shell = shell;
        Available = CollectionViewSource.GetDefaultView(All);
        Available.Filter = o => o is LiveParameter p &&
                                (_search.Length == 0 || p.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)) &&
                                (!_onlySupported || p.Custom is not null || p.Obd is not null && _supported.Contains(p.Obd.Pid));
        Available.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LiveParameter.Group)));

        foreach (var p in ObdPids.All)
            AddParam(new LiveParameter { Key = p.Key, Name = p.Name, MetricUnit = p.Unit, Kind = p.Kind, Obd = p });
        LoadCustom();

        StartStopCommand = new RelayCommand(() => { if (Running) Stop(); else Start(); }, () => _shell.HasBus || Running);
        LoadSupportedCommand = new AsyncCommand(LoadSupportedAsync, () => _shell.HasBus && !Running);
        RecordCommand = new RelayCommand(ToggleRecord, () => Watching.Count > 0);
        ResetCommand = new RelayCommand(() => { foreach (var p in Watching) p.ResetStats(); ChartVersion++; });
        ClearSelectionCommand = new RelayCommand(() => { foreach (var p in Watching.ToList()) p.IsSelected = false; });
        ReloadCustomCommand = new RelayCommand(LoadCustom, () => !Running);
        OpenCustomFileCommand = new RelayCommand(() => OpenInEditor(Paths.CustomPidsFile));

        _shell.StopBackgroundWork += Stop;
        foreach (var key in _shell.Settings.LastLivePids)
            if (All.FirstOrDefault(p => p.Key == key) is LiveParameter p) p.IsSelected = true;
        if (Watching.Count == 0)
            foreach (var key in new[] { "0C", "0D", "05", "11", "04", "42" })
                if (All.FirstOrDefault(p => p.Key == key) is LiveParameter p) p.IsSelected = true;
    }

    public static void OpenInEditor(string path)
    {
        try
        {
            if (path == Paths.CustomPidsFile && !File.Exists(path)) UserFiles.LoadCustomParameters();
            if (path == Paths.FunctionsFile && !File.Exists(path)) UserFiles.LoadFunctions();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    private void AddParam(LiveParameter p)
    {
        p.SetUnitLabel(_shell.Settings.UsUnits);
        p.SelectionChanged += OnSelection;
        p.GraphChanged += OnGraph;
        All.Add(p);
    }

    private void LoadCustom()
    {
        foreach (var p in All.Where(p => p.Custom is not null).ToList())
        {
            p.IsSelected = false;
            All.Remove(p);
        }
        try
        {
            foreach (var c in UserFiles.LoadCustomParameters())
                AddParam(new LiveParameter { Key = $"C:{c.ModuleHex}:{c.IdentifierHex}:{c.Name}", Name = c.Name, MetricUnit = c.Unit, Custom = c });
        }
        catch (Exception e) { _shell.Write("custom-parameters.json: " + e.Message); }
    }

    private void OnSelection(LiveParameter p)
    {
        if (p.IsSelected && !Watching.Contains(p)) Watching.Add(p);
        if (!p.IsSelected) { Watching.Remove(p); p.IsGraphed = false; }
        _shell.Settings.LastLivePids = Watching.Select(x => x.Key).ToList();
        if (p.IsSelected && Series.Count < 4 && !p.IsGraphed) p.IsGraphed = true;
    }

    private void OnGraph(LiveParameter p)
    {
        if (p.IsGraphed)
        {
            if (Series.Count >= 6) { p.IsGraphed = false; _shell.Status = "Up to 6 values can be graphed at once."; return; }
            var used = Series.Select(s => s.Color).ToHashSet();
            var color = Palette.FirstOrDefault(c => !used.Contains(c));
            p.Series = new ChartSeries(p.Name, color, 600);
            Series.Add(p.Series);
        }
        else if (p.Series is not null)
        {
            Series.Remove(p.Series);
            p.Series = null;
        }
        ChartVersion++;
    }

    private async Task LoadSupportedAsync()
    {
        var set = await _shell.Run("Read supported data", s => s.SupportedPids());
        if (set is null) return;
        _supported = set.ToHashSet();
        OnlySupported = true;
        Available.Refresh();
        _shell.Status = $"The engine module supports {All.Count(p => p.Obd is not null && _supported.Contains(p.Obd.Pid))} of the listed values.";
    }

    private void Start()
    {
        if (Watching.Count == 0) { Dialogs.Info("Tick one or more values in the list first."); return; }
        if (_shell.MonitorActive) { Dialogs.Info("Stop the bus monitor first."); return; }
        _cts = new CancellationTokenSource();
        Running = true;
        _ = Loop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        Running = false;
        Rate = "";
    }

    private async Task Loop(CancellationToken ct)
    {
        var session = _shell.Session;
        if (session is null) { Running = false; return; }
        _shell.Status = "Live data running…";
        var sw = Stopwatch.StartNew();
        int frames = 0;
        int errors = 0;
        while (!ct.IsCancellationRequested)
        {
            var obd = Watching.Where(p => p.Obd is not null).Select(p => p.Obd!.Pid).Distinct().ToList();
            var custom = Watching.Where(p => p.Custom is not null).Select(p => p.Custom!).ToList();
            try
            {
                var (values, customValues) = await _shell.Worker.Run(() =>
                {
                    var v = obd.Count > 0 ? session.ReadPids(obd) : new Dictionary<byte, byte[]>();
                    var c = new Dictionary<CustomParameter, double?>();
                    foreach (var p in custom) c[p] = session.ReadCustom(p);
                    return (v, c);
                });
                errors = 0;
                bool us = _shell.Settings.UsUnits;
                foreach (var p in Watching.ToList())
                {
                    if (p.Obd is not null && values.TryGetValue(p.Obd.Pid, out var data) && data.Length >= p.Obd.Bytes)
                        p.Update(p.Obd.Decode(data), us);
                    else if (p.Custom is not null && customValues.TryGetValue(p.Custom, out var cv) && cv is double d)
                        p.Update(d, us);
                }
                WriteCsvRow();
                ChartVersion++;
                frames++;
                if (sw.ElapsedMilliseconds > 1000)
                {
                    Rate = $"{frames * 1000.0 / sw.ElapsedMilliseconds:0.0} updates/s";
                    sw.Restart();
                    frames = 0;
                }
            }
            catch (Exception e)
            {
                if (++errors >= 5)
                {
                    _shell.Status = "Live data stopped: " + e.Message;
                    _shell.Write("Live data stopped: " + e.Message);
                    break;
                }
            }
            try { await Task.Delay(Math.Max(0, _shell.Settings.PollDelayMs), ct); } catch (TaskCanceledException) { }
        }
        Running = false;
        Rate = "";
        if (_shell.Status == "Live data running…") _shell.Status = "Live data stopped.";
    }

    private void ToggleRecord()
    {
        if (_csv is not null)
        {
            _csv.Dispose();
            _csv = null;
            RecordInfo = "Saved to " + Paths.Recordings;
        }
        else
        {
            var path = Path.Combine(Paths.Recordings, $"live-{_shell.Vin ?? "vehicle"}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _csv = new StreamWriter(path) { AutoFlush = false };
            _csvColumns = Watching.ToList();
            _csv.WriteLine("time_s," + string.Join(",", _csvColumns.Select(c => $"\"{c.Name} ({c.Unit})\"")));
            _csvClock.Restart();
            RecordInfo = "Recording to " + Path.GetFileName(path);
            if (!Running) Start();
        }
        Raise(nameof(Recording));
        Raise(nameof(RecordText));
    }

    private void WriteCsvRow()
    {
        if (_csv is null) return;
        var t = _csvClock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        _csv.WriteLine(t + "," + string.Join(",", _csvColumns.Select(c => c.Last?.ToString("0.###", CultureInfo.InvariantCulture) ?? "")));
        if (_csvClock.ElapsedMilliseconds % 2000 < 200) _csv.Flush();
    }

    /// <summary>Called when the unit setting changes.</summary>
    public void RefreshUnits()
    {
        foreach (var p in All) { p.SetUnitLabel(_shell.Settings.UsUnits); p.ResetStats(); }
    }
}
