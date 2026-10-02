using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TlbbAgent.App.ViewModels;

namespace TlbbAgent.App;
public partial class MainWindow : Window
{
    private bool _draggingCrosshair;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => PreferRoundedWindowCorners();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void PreferRoundedWindowCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        var preference = 2; // DWMWCP_ROUND
        var handle = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int valueSize);
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnCrosshairDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IInputElement el) return;
        _draggingCrosshair = true;
        el.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        el.MouseLeftButtonUp += OnCrosshairUp;
    }

    private void OnCrosshairUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingCrosshair || sender is not IInputElement el) return;
        _draggingCrosshair = false;
        el.MouseLeftButtonUp -= OnCrosshairUp;
        el.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;

        var p = PointToScreen(e.GetPosition(this));
        Vm?.BindingPanel.BindAtScreenPoint(p.X, p.Y);
    }
}
