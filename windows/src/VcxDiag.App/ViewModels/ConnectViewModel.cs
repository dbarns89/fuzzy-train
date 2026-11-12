using System.Collections.ObjectModel;
using System.Windows.Input;
using VcxDiag.Core;
using VcxDiag.Core.J2534;
using VcxDiag.Core.Protocols;
using VcxDiag.Core.Simulation;
using VcxDiag.Core.Transport;

namespace VcxDiag.App.ViewModels;

public sealed class InterfaceOption
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public J2534Driver? Driver { get; init; }
    public string? DllPath { get; init; }
    public bool IsSimulator { get; init; }
    public override string ToString() => Title;
}

public sealed record BusOption(string Title, string Detail, BusKind? Kind)
{
    public override string ToString() => Title;
}

public sealed class ConnectViewModel : ObservableObject
{
    private readonly Shell _shell;
    public Shell Shell => _shell;

    public ObservableCollection<InterfaceOption> Interfaces { get; } = new();

    public IReadOnlyList<BusOption> Buses { get; } = new BusOption[]
    {
        new("Automatic (recommended)", "Tries GMLAN high speed CAN, then Class 2.", null),
        new(BusKind.Class2.Title(), "Most GM cars and trucks 1996-2007.", BusKind.Class2),
        new(BusKind.GmlanHighSpeed.Title(), "Engine/transmission on 2005+, all modules on most 2008+.", BusKind.GmlanHighSpeed),
        new(BusKind.GmlanSingleWire.Title(), "Body, radio, HVAC and cluster on many 2005+ GM models.", BusKind.GmlanSingleWire),
        new(BusKind.GmlanMediumSpeed.Title(), "Some Saturn / Opel / Saab models. Interface must support pin switching.", BusKind.GmlanMediumSpeed),
    };

    private InterfaceOption? _selectedInterface;
    public InterfaceOption? SelectedInterface
    {
        get => _selectedInterface;
        set { Set(ref _selectedInterface, value); Raise(nameof(InterfaceWarning)); }
    }

    private BusOption _selectedBus;
    public BusOption SelectedBus { get => _selectedBus; set => Set(ref _selectedBus, value); }

    public string InterfaceWarning
    {
        get
        {
            var d = _selectedInterface?.Driver;
            if (d is null || d.MatchesProcess) return "";
            return $"This driver is {(d.Is64Bit ? "64" : "32")}-bit but this copy of VCX Diag is {(Environment.Is64BitProcess ? "64" : "32")}-bit. " +
                   $"Use the {(d.Is64Bit ? "x64" : "x86")} download of VCX Diag with this driver.";
        }
    }

    private string _firmware = "", _dllVersion = "", _api = "";
    public string Firmware { get => _firmware; set => Set(ref _firmware, value); }
    public string DllVersion { get => _dllVersion; set => Set(ref _dllVersion, value); }
    public string Api { get => _api; set => Set(ref _api, value); }

    public bool UsUnits
    {
        get => _shell.Settings.UsUnits;
        set { _shell.Settings.UsUnits = value; Raise(); _shell.Settings.Save(); _shell.RaiseUnitsChanged(); }
    }

    public bool LogBusTraffic
    {
        get => _shell.Settings.LogBusTraffic;
        set { _shell.Settings.LogBusTraffic = value; Raise(); _shell.Settings.Save(); }
    }

    public string AppVersion => $"Version {typeof(ConnectViewModel).Assembly.GetName().Version?.ToString(3)} ({(Environment.Is64BitProcess ? "64" : "32")}-bit)";

    public ICommand RefreshCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ReadBatteryCommand { get; }

    /// <summary>Raised after a vehicle bus opens so the Vehicle page can load the overview.</summary>
    public event Action? Connected;

    public ConnectViewModel(Shell shell)
    {
        _shell = shell;
        _selectedBus = Buses[0];
        if (shell.Settings.LastBus >= 0 && shell.Settings.LastBus < Buses.Count) _selectedBus = Buses[shell.Settings.LastBus];
        RefreshCommand = new RelayCommand(Refresh);
        BrowseCommand = new RelayCommand(Browse);
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !_shell.IsConnected && SelectedInterface is not null);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => _shell.IsConnected);
        ReadBatteryCommand = new AsyncCommand(ReadBatteryAsync, () => _shell.IsConnected);
        Refresh();
    }

    private void Refresh()
    {
        Interfaces.Clear();
        foreach (var d in DriverRegistry.Find())
            Interfaces.Add(new InterfaceOption
            {
                Title = d.Name,
                Detail = $"{d.Vendor}  •  {(d.Is64Bit ? "64" : "32")}-bit  •  {Path.GetFileName(d.DllPath)}" +
                         (d.Protocols.Count > 0 ? $"  •  {string.Join(", ", d.Protocols)}" : ""),
                Driver = d,
                DllPath = d.DllPath,
            });
        Interfaces.Add(new InterfaceOption
        {
            Title = "Demo vehicle (no hardware)",
            Detail = "Built-in simulator: try every feature without a car.",
            IsSimulator = true,
        });
        SelectedInterface = Interfaces.FirstOrDefault(i => i.DllPath is not null &&
                                string.Equals(i.DllPath, _shell.Settings.LastDriverDll, StringComparison.OrdinalIgnoreCase))
                            ?? Interfaces.FirstOrDefault(i => i.Driver?.LooksLikeVcx == true && i.Driver.MatchesProcess)
                            ?? Interfaces.FirstOrDefault(i => i.Driver?.MatchesProcess == true)
                            ?? Interfaces.First();
        if (Interfaces.Count == 1)
            _shell.Status = "No J2534 driver found. Install VX Manager and its GM/J2534 driver, or use the demo vehicle.";
    }

    private void Browse()
    {
        var path = Dialogs.OpenFile("J2534 driver (*.dll)|*.dll", @"C:\Program Files (x86)");
        if (path is null) return;
        var is64 = PeInfo.Is64Bit(path) ?? false;
        var opt = new InterfaceOption
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Detail = $"Chosen manually  •  {(is64 ? "64" : "32")}-bit  •  {path}",
            Driver = new J2534Driver(Path.GetFileNameWithoutExtension(path), "", path, is64, Array.Empty<string>()),
            DllPath = path,
        };
        Interfaces.Insert(0, opt);
        SelectedInterface = opt;
    }

    private async Task ConnectAsync()
    {
        var opt = SelectedInterface!;
        if (!string.IsNullOrEmpty(InterfaceWarning)) { Dialogs.Error(InterfaceWarning); return; }
        _shell.Status = "Opening interface…";
        _shell.Busy = true;
        try
        {
            var session = await _shell.Worker.Run(() =>
            {
                IPassThruDevice dev = opt.IsSimulator ? new SimulatedDevice() : new J2534Device(opt.DllPath!, opt.Title);
                return new DiagnosticSession(dev) { Log = _shell.Trace };
            });
            _shell.Session = session;
            _shell.DeviceText = opt.Title;
            _shell.Write($"Opened interface: {opt.Title}");
            _shell.Settings.LastDriverDll = opt.DllPath;
            _shell.Settings.LastBus = Buses.ToList().IndexOf(SelectedBus);
            _shell.Settings.Save();

            var (ver, volts) = await _shell.Worker.Run(() =>
            {
                DeviceVersion? v = null;
                try { v = session.Device.ReadVersion(); } catch { }
                return (v, session.Device.ReadBatteryVolts());
            });
            Firmware = ver?.Firmware ?? "?";
            DllVersion = ver?.Dll ?? "?";
            Api = ver?.Api ?? "?";
            _shell.Battery = volts;
            if (volts is < 11.0) _shell.Write($"Warning: battery voltage is low ({volts:0.0} V). Connect a charger for long sessions.");

            _shell.Status = "Connecting to the vehicle…";
            BusKind? kind = SelectedBus.Kind;
            if (kind is null)
            {
                kind = await _shell.Worker.Run(() => session.AutoDetect());
                if (kind is null)
                {
                    _shell.NotifyBusChanged();
                    _shell.Status = "Interface open, but no vehicle answered.";
                    Dialogs.Error("The interface is working, but no vehicle answered on GMLAN or Class 2.\n\n" +
                                  "• Turn the ignition ON (engine can be off).\n• Check the OBD plug is fully seated.\n" +
                                  "• Pick the vehicle network manually and connect again.");
                    return;
                }
            }
            else
            {
                await _shell.Worker.Run(() => session.OpenBus(kind.Value));
            }
            _shell.NotifyBusChanged();
            _shell.Status = $"Connected: {kind.Value.Title()}";
            _shell.Write($"Connected to {kind.Value.Title()}");
            Connected?.Invoke();
        }
        catch (Exception e)
        {
            _shell.Write("Connect failed: " + e.Message);
            _shell.Status = "Connect failed.";
            var s = _shell.Session;
            _shell.Session = null;
            if (s is not null) await _shell.Worker.Run(() => s.Dispose());
            _shell.NotifyBusChanged();
            Dialogs.Error("Could not connect.\n\n" + e.Message +
                          "\n\nCheck that the VCX Nano is plugged in, VX Manager shows it as connected, and no other diagnostic program is using it.");
        }
        finally
        {
            _shell.Busy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        _shell.StopBackground();
        var s = _shell.Session;
        _shell.Session = null;
        if (s is not null) await _shell.Worker.Run(() => s.Dispose());
        _shell.Modules.Clear();
        _shell.Vin = null;
        _shell.Battery = null;
        _shell.DeviceText = "No interface";
        _shell.NotifyBusChanged();
        _shell.Status = "Disconnected.";
        _shell.Write("Disconnected");
        Firmware = DllVersion = Api = "";
    }

    private async Task ReadBatteryAsync()
    {
        _shell.Battery = await _shell.Run("Read battery voltage", s => s.Device.ReadBatteryVolts());
    }
}
