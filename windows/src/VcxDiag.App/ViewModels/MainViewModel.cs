using System.Windows.Input;

namespace VcxDiag.App.ViewModels;

public sealed record NavItem(string Title, string Glyph, string Hint, object Page);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public Shell Shell { get; } = new();
    public IReadOnlyList<NavItem> Pages { get; }

    private NavItem _current;
    public NavItem Current { get => _current; set => Set(ref _current, value); }

    public ConnectViewModel Connect { get; }
    public ICommand QuickDisconnectCommand { get; }

    public MainViewModel()
    {
        Connect = new ConnectViewModel(Shell);
        var vehicle = new VehicleViewModel(Shell);
        var modules = new ModulesViewModel(Shell);
        var codes = new CodesViewModel(Shell);
        var live = new LiveDataViewModel(Shell);
        var emissions = new EmissionsViewModel(Shell);
        var monitor = new MonitorViewModel(Shell);
        var terminal = new TerminalViewModel(Shell);
        var report = new ReportViewModel(Shell, vehicle, emissions);

        // Segoe MDL2 Assets glyphs (built into Windows 10/11).
        Pages = new NavItem[]
        {
            new("Connect", "", "Interface and vehicle network", Connect),
            new("Vehicle", "", "VIN, emissions status, health check", vehicle),
            new("Modules", "", "Find modules and read their identity", modules),
            new("Trouble codes", "", "Read and clear codes", codes),
            new("Live data", "", "Values, graphs and recording", live),
            new("Emissions", "", "Freeze frame, readiness, test results", emissions),
            new("Functions", "", "Special functions and expert terminal", terminal),
            new("Bus monitor", "", "Watch raw network traffic", monitor),
            new("Report & log", "", "Save a report, view the session log", report),
        };
        _current = Pages[0];

        Connect.Connected += async () =>
        {
            Current = Pages[1];
            await vehicle.RefreshAsync();
        };
        Shell.UnitsChanged += live.RefreshUnits;
        QuickDisconnectCommand = new AsyncCommand(Connect.DisconnectAsync, () => Shell.IsConnected);
    }

    public void Dispose() => Shell.Dispose();
}
