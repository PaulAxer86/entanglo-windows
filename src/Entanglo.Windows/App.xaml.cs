using Entanglo.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;

namespace Entanglo.Windows;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static AppServices Services { get; private set; } = null!;
    public static ClipboardBridge? Clipboard { get; private set; }
    public static TrayIconHost? Tray { get; private set; }

    // Static instance avoids relying on Application.Current which can silently
    // return null when the ContextFlyout click fires while the window is hidden.
    static App? _instance;

    static bool _exiting;

    public App()
    {
        _instance = this;
        InitializeComponent();
    }

    // Called by TrayIconHost — no dependency on Application.Current.
    public static void RequestQuit() => _instance?.QuitEntanglo();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices();

        MainWindow = new MainWindow();

        // Close button hides the window; services keep running so the link
        // (input sharing, clipboard, discovery) stays alive in the background.
        // Use QuitEntanglo() (or restart) for an actual exit.
        MainWindow.AppWindow.Closing += (_, closingArgs) =>
        {
            if (_exiting) return;
            closingArgs.Cancel = true;
            MainWindow!.AppWindow.Hide();
        };

        MainWindow.Activate();

        Clipboard = new ClipboardBridge(Services.Coordinator, MainWindow.DispatcherQueue);
        Tray      = new TrayIconHost();

        AppInstance.GetCurrent().Activated += (_, _) =>
            MainWindow.DispatcherQueue.TryEnqueue(ShowMainWindow);

        // Peer-pushed installer arrived — show the auto-install countdown.
        Services.Updates.PeerPushedInstallerReady += (version, path, peerName) =>
            MainWindow.DispatcherQueue.TryEnqueue(() =>
                _ = ShowPeerUpdateCountdownAsync(version, path, peerName));

        _ = Services.StartAsync();
    }

    static async Task ShowPeerUpdateCountdownAsync(string version, string installerPath, string peerName)
    {
        if (MainWindow?.Content is not FrameworkElement root) return;
        ShowMainWindow();

        var progress = new ProgressBar { Minimum = 0, Maximum = 10, Value = 10, Height = 6 };
        var subtitle = new TextBlock
        {
            Text = $"{peerName} pushed Entanglo {version}. Installing in 10s…",
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(subtitle);
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            Title = $"Update {version} from {peerName}",
            Content = panel,
            PrimaryButtonText = "Install now",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root.XamlRoot,
        };

        var cts = new CancellationTokenSource();
        var showTask = dialog.ShowAsync().AsTask();

        // Countdown loop — closes the dialog with Primary result at zero.
        _ = Task.Run(async () =>
        {
            for (int s = 10; s > 0; s--)
            {
                try { await Task.Delay(1000, cts.Token); }
                catch (TaskCanceledException) { return; }
                MainWindow!.DispatcherQueue.TryEnqueue(() =>
                {
                    progress.Value = s - 1;
                    subtitle.Text = $"{peerName} pushed Entanglo {version}. Installing in {s - 1}s…";
                });
            }
            MainWindow!.DispatcherQueue.TryEnqueue(() =>
            {
                dialog.Hide();
                RunPeerInstall(installerPath);
            });
        });

        var result = await showTask;
        cts.Cancel();
        if (result == ContentDialogResult.Primary)
            RunPeerInstall(installerPath);
    }

    static void RunPeerInstall(string installerPath)
    {
        Services.Updates.RunInstallerAndQuit(installerPath, () =>
            MainWindow?.DispatcherQueue.TryEnqueue(() =>
                (Application.Current as App)?.QuitEntanglo()));
    }

    public static void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.AppWindow.Show();
        MainWindow.Activate();
    }

    public void QuitEntanglo()
    {
        if (_exiting) return;
        _exiting = true;

        AppLog.Info("[App] QuitEntanglo — starting shutdown");

        // Raw OS thread watchdog: unconditionally kills the process after 2 s.
        new Thread(() => { Thread.Sleep(2_000); Environment.Exit(0); })
            { IsBackground = true, Name = "EntangloQuitWatchdog" }.Start();

        try { Tray?.Dispose();      } catch { }
        try { Clipboard?.Dispose(); } catch { }
        try { Services.Dispose();   } catch { }
        AppLog.Info("[App] Cleanup done — exiting");
        Environment.Exit(0);
    }

    // Back-compat shim — Settings still wires to the older name.
    public void QuitForUpdate() => QuitEntanglo();
}
