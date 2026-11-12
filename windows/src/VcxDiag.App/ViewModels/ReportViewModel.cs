using System.Diagnostics;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Input;
using VcxDiag.Core.Gm;

namespace VcxDiag.App.ViewModels;

/// <summary>Session log and a printable HTML vehicle report.</summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly Shell _shell;
    private readonly VehicleViewModel _vehicle;
    private readonly EmissionsViewModel _emissions;
    public Shell Shell => _shell;

    public ICommand SaveReportCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public ReportViewModel(Shell shell, VehicleViewModel vehicle, EmissionsViewModel emissions)
    {
        _shell = shell;
        _vehicle = vehicle;
        _emissions = emissions;
        SaveReportCommand = new RelayCommand(SaveReport);
        CopyLogCommand = new RelayCommand(() => Clipboard.SetText(string.Join(Environment.NewLine, _shell.Log)));
        OpenFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(Paths.Documents) { UseShellExecute = true }));
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private void SaveReport()
    {
        var path = Path.Combine(Paths.Reports, $"report-{_shell.Vin ?? "vehicle"}-{DateTime.Now:yyyyMMdd-HHmm}.html");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Vehicle report</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#1d2733}h1{margin:0 0 4px}h2{margin-top:28px;border-bottom:2px solid #1d6fb8;padding-bottom:4px}")
          .Append("table{border-collapse:collapse;width:100%;margin-top:8px}td,th{border:1px solid #d5dbe3;padding:6px 8px;text-align:left;font-size:14px}th{background:#eef3f8}")
          .Append(".muted{color:#5b6b7c}.bad{color:#c0392b;font-weight:600}.ok{color:#1e8449}</style></head><body>");
        sb.Append($"<h1>Vehicle diagnostic report</h1><div class=muted>{DateTime.Now:f} • VCX Diag • {E(_shell.BusText)}</div>");

        sb.Append("<h2>Vehicle</h2><table>");
        foreach (var r in _vehicle.Identity) sb.Append($"<tr><th>{E(r.Label)}</th><td>{E(r.Value)}</td></tr>");
        if (_vehicle.Identity.Count == 0) sb.Append($"<tr><th>VIN</th><td>{E(_shell.VinText)}</td></tr>");
        sb.Append($"<tr><th>Emissions status</th><td>{E(_vehicle.MilText)}</td></tr>");
        foreach (var r in _vehicle.Calibrations) sb.Append($"<tr><th>{E(r.Label)}</th><td>{E(r.Value)}</td></tr>");
        sb.Append("</table>");

        sb.Append("<h2>Modules</h2>");
        if (_shell.Modules.Count == 0) sb.Append("<p class=muted>No module scan in this session.</p>");
        else
        {
            sb.Append("<table><tr><th>Module</th><th>Address</th><th>Codes</th></tr>");
            foreach (var m in _shell.Modules)
                sb.Append($"<tr><td>{E(m.Name)}</td><td>{E(m.AddressText)}</td><td>{(m.DtcCount is int n ? n.ToString() : "not read")}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Trouble codes</h2>");
        if (_shell.Codes.Count == 0) sb.Append("<p class=ok>No trouble codes recorded in this session.</p>");
        else
        {
            sb.Append("<table><tr><th>Code</th><th>Module</th><th>Description</th><th>Failure type</th><th>Status</th></tr>");
            foreach (var d in _shell.Codes)
                sb.Append($"<tr><td class=bad>{E(d.FullCode)}</td><td>{E(d.Module)}</td><td>{E(d.Description)}</td><td>{E(d.FailureTypeText)}</td><td>{E(d.StatusText)}</td></tr>");
            sb.Append("</table>");
        }

        if (_emissions.Monitors.Count > 0)
        {
            sb.Append($"<h2>Readiness monitors</h2><p>{E(_emissions.ReadinessTitle)}</p><table><tr><th>Monitor</th><th>State</th></tr>");
            foreach (var m in _emissions.Monitors.Where(m => m.Supported))
                sb.Append($"<tr><td>{E(m.Name)}</td><td class={(m.Complete ? "ok" : "bad")}>{E(m.State)}</td></tr>");
            sb.Append("</table>");
        }
        if (_emissions.FreezeFrame.Count > 0)
        {
            sb.Append($"<h2>Freeze frame</h2><p>{E(_emissions.FreezeTitle)}</p><table><tr><th>Value</th><th>Reading</th></tr>");
            foreach (var v in _emissions.FreezeFrame) sb.Append($"<tr><td>{E(v.Name)}</td><td>{E(v.Value)} {E(v.Unit)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<p class=muted style=\"margin-top:32px\">Generic SAE descriptions. Manufacturer-specific codes need the vehicle's service information.</p></body></html>");
        File.WriteAllText(path, sb.ToString());
        _shell.Status = "Report saved: " + path;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
