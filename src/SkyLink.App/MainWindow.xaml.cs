using System.ComponentModel;
using Wpf.Ui.Controls;

namespace SkyLink.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        // Dev/testing: SKYLINK_WINDOW="x,y,width,height" places the window (two copies side by side).
        if (Environment.GetEnvironmentVariable("SKYLINK_WINDOW") is { Length: > 0 } w
            && w.Split(',') is [var x, var y, var width, var height])
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            Left = double.Parse(x);
            Top = double.Parse(y);
            Width = double.Parse(width);
            Height = double.Parse(height);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Dispose(); // leaves the party and never leaves the aircraft frozen
        base.OnClosing(e);
    }
}
