using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace VcxDiag.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crash(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Crash(a.Exception, showDialog: false); a.SetObserved(); };
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Crash(e.Exception);
        e.Handled = true; // keep the app running; most errors are a failed bus operation
    }

    private static void Crash(Exception? ex, bool showDialog = true)
    {
        if (ex is null) return;
        try { File.AppendAllText(Path.Combine(Paths.Logs, "errors.log"), $"{DateTime.Now:O}\n{ex}\n\n"); } catch { }
        if (showDialog)
            MessageBox.Show($"Something went wrong:\n\n{ex.Message}\n\nDetails were saved to Documents\\VCX Diag\\Logs\\errors.log.",
                "VCX Diag", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the value is non-null and not an empty string.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
