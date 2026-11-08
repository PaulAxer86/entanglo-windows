using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Entanglo.Windows.Pages;
using WinSize = global::Windows.Graphics.SizeInt32;

namespace Entanglo.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SetMicaBackdrop();
        AppWindow.Resize(new WinSize(900, 620));
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange) return;
            if (AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized) return;
            var s = AppWindow.Size;
            if (s.Width < 800 || s.Height < 560)
                AppWindow.Resize(new WinSize(Math.Max(s.Width, 800), Math.Max(s.Height, 560)));
        };
    }

    void SetMicaBackdrop()
    {
        // Mica gives subtle theme-aware translucency (light/dark from system).
        // BaseAlt = the recommended "not too transparent" variant for content
        // surfaces with a NavigationView shell.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        else if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nv && (nv.Tag as string) == "Dashboard")
            {
                NavView.SelectedItem = nv;
                return;
            }
        }
    }

    void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) return;
        Type? page = (item.Tag as string) switch
        {
            "Dashboard"    => typeof(DashboardPage),
            "Devices"      => typeof(DevicesPage),
            "InputSharing" => typeof(InputSharingPage),
            "Files"        => typeof(FilesPage),
            "Print"        => typeof(PrintPage),
            "Network"      => typeof(NetworkPage),
            "Updates"      => typeof(UpdatesPage),
            "Settings"     => typeof(SettingsPage),
            "Logs"         => typeof(LogsPage),
            _              => null,
        };
        if (page != null) ContentFrame.Navigate(page);
    }
}
