using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using VcxDiag.Core;
using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;
using VcxDiag.Core.Protocols;
using VcxDiag.Core.Transport;

namespace VcxDiag.App.ViewModels;

/// <summary>
/// State shared by every page: the open interface and bus, what we've learned about the
/// vehicle, and the session log. All bus work goes through <see cref="Run{T}"/>.
/// </summary>
public sealed class Shell : ObservableObject, IDisposable
{
    public DiagWorker Worker { get; } = new();
    public Settings Settings { get; } = Settings.Load();
    public Dispatcher Ui { get; } = Application.Current.Dispatcher;

    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<EcuModule> Modules { get; } = new();
    public ObservableCollection<Dtc> Codes { get; } = new();

    private readonly StreamWriter? _logFile;

    public Shell()
    {
        try
        {
            _logFile = new StreamWriter(Path.Combine(Paths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log")) { AutoFlush = true };
        }
        catch { _logFile = null; }
        try
        {
            var n = DtcDescriptions.LoadUserFile(Paths.DtcFile);
            n += DtcDescriptions.LoadUserFile(Path.Combine(AppContext.BaseDirectory, "dtc-descriptions.csv"));
            if (n > 0) Write($"Loaded {n} code description(s) from dtc-descriptions.csv");
        }
        catch (Exception e) { Write("Could not read dtc-descriptions.csv: " + e.Message); }
    }

    // ---------------------------------------------------------------- connection state

    private DiagnosticSession? _session;
    public DiagnosticSession? Session
    {
        get => _session;
        set { if (Set(ref _session, value)) { Raise(nameof(IsConnected)); Raise(nameof(HasBus)); } }
    }

    public bool IsConnected => _session is not null;
    public bool HasBus => _session?.Bus is not null;
    public bool IsCan => _session?.Bus?.Kind.IsCan() ?? false;

    private string _deviceText = "No interface";
    public string DeviceText { get => _deviceText; set => Set(ref _deviceText, value); }

    private string _busText = "Not connected";
    public string BusText { get => _busText; set => Set(ref _busText, value); }

    private string? _vin;
    public string? Vin { get => _vin; set { Set(ref _vin, value); Raise(nameof(VinText)); } }
    public string VinText => _vin ?? "VIN unknown";

    private double? _battery;
    public double? Battery { get => _battery; set { Set(ref _battery, value); Raise(nameof(BatteryText)); } }
    public string BatteryText => _battery is double v ? $"{v:0.0} V" : "-- V";

    private string _status = "Ready. Connect to your interface to begin.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    /// <summary>Set while the bus monitor owns the bus (filters are opened up).</summary>
    public bool MonitorActive { get; set; }

    /// <summary>Live data stops itself when this is raised (before connect/disconnect or monitor).</summary>
    public event Action? StopBackgroundWork;
    public void StopBackground() => StopBackgroundWork?.Invoke();

    public event Action? UnitsChanged;
    public void RaiseUnitsChanged() => UnitsChanged?.Invoke();

    public void NotifyBusChanged()
    {
        Raise(nameof(HasBus));
        Raise(nameof(IsCan));
        BusText = _session?.Bus?.Kind.Title() ?? (IsConnected ? "Interface open, no vehicle bus" : "Not connected");
    }

    // ---------------------------------------------------------------- running work

    /// <summary>Runs bus work on the worker thread with status, busy indicator and error handling.</summary>
    public async Task<T?> Run<T>(string what, Func<DiagnosticSession, T> work, bool quiet = false)
    {
        if (_session is null) { Dialogs.Info("Connect to the interface and vehicle first (Connect page)."); return default; }
        if (MonitorActive) { Dialogs.Info("Stop the bus monitor first. It holds the bus open for listening."); return default; }
        var s = _session;
        if (!quiet) { Status = what + "…"; Busy = true; }
        try
        {
            var result = await Worker.Run(() => work(s));
            if (!quiet) Status = what + ": done.";
            return result;
        }
        catch (Exception e)
        {
            Status = what + ": " + e.Message;
            Write($"{what} failed: {e.Message}");
            if (!quiet) Dialogs.Error($"{what} failed.\n\n{e.Message}");
            return default;
        }
        finally
        {
            if (!quiet) Busy = false;
        }
    }

    public Task<bool> Run(string what, Action<DiagnosticSession> work, bool quiet = false) =>
        Run(what, s => { work(s); return true; }, quiet);

    // ---------------------------------------------------------------- log

    public void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        try { _logFile?.WriteLine(stamped); } catch { }
        if (Ui.CheckAccess()) AddLine(stamped);
        else Ui.BeginInvoke(() => AddLine(stamped));
    }

    private void AddLine(string s)
    {
        Log.Add(s);
        while (Log.Count > 3000) Log.RemoveAt(0);
    }

    /// <summary>Bus trace from the session. Only logged when the user turned it on.</summary>
    public void Trace(string s)
    {
        if (Settings.LogBusTraffic || !(s.StartsWith("TX ") || s.StartsWith("RX "))) Write(s);
    }

    public void Dispose()
    {
        StopBackground();
        try { Worker.Run(() => _session?.Dispose()).Wait(2000); } catch { }
        Worker.Dispose();
        _logFile?.Dispose();
        Settings.Save();
    }
}
