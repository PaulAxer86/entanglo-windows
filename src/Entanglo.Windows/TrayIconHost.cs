using Entanglo.Core.Logging;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Entanglo.Windows;

public sealed class TrayIconHost : IDisposable
{
    readonly TaskbarIcon _icon;
    bool _disposed;

    public TrayIconHost()
    {
        var show = new MenuFlyoutItem { Text = "Show Entanglo" };
        show.Click += (_, _) => App.ShowMainWindow();

        var quit = new MenuFlyoutItem { Text = "Quit Entanglo" };
        quit.Click += (_, _) =>
        {
            AppLog.Info("[Tray] Quit clicked");
            // Kill thread starts immediately inside the click handler — independent
            // of Application.Current, App._instance, or any WinUI dispatch state.
            // If everything else fails, this exits the process within 500 ms.
            new Thread(() => { Thread.Sleep(500); Environment.Exit(0); })
                { IsBackground = true, Name = "TrayQuitKiller" }.Start();
            App.RequestQuit();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(show);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Entanglo.ico");
        var iconUri  = new Uri("file:///" + iconPath.Replace('\\', '/'));
        _icon = new TaskbarIcon
        {
            ToolTipText = "Entanglo",
            IconSource  = new BitmapImage(iconUri),
            ContextFlyout = menu,
            NoLeftClickDelay = true,
        };
        _icon.LeftClickCommand = new RelayCommand(_ => App.ShowMainWindow());
        _icon.ForceCreate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _icon.Dispose(); } catch { /* shutdown race */ }
    }

    sealed class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Action<object?> _exec;
        public RelayCommand(Action<object?> exec) => _exec = exec;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _exec(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
