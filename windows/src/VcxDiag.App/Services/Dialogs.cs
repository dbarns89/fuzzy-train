using System.Windows;
using Microsoft.Win32;

namespace VcxDiag.App;

public static class Dialogs
{
    private const string Title = "VCX Diag";

    public static void Error(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Info(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Yes/No confirmation for anything that changes the vehicle.</summary>
    public static bool Confirm(string message, string heading = "Please confirm") =>
        MessageBox.Show(Application.Current?.MainWindow!, message, $"{Title} - {heading}",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static string? SaveFile(string filter, string defaultName)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = defaultName, InitialDirectory = Paths.Documents };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? OpenFile(string filter, string? initialDir = null)
    {
        var d = new OpenFileDialog { Filter = filter };
        if (initialDir is not null && Directory.Exists(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog() == true ? d.FileName : null;
    }
}
