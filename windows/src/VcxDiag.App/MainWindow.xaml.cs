using System.ComponentModel;
using System.Windows;
using VcxDiag.App.ViewModels;

namespace VcxDiag.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Title = $"VCX Diag {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Dispose();
        base.OnClosing(e);
    }
}
