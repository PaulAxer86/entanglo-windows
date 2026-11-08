using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Entanglo.Windows.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var s = App.Services.Settings;
        ReceiveInputToggle.IsOn = s.ReceiveRemoteInput;
        AutoDialToggle.IsOn     = s.AutoDialTrusted;

        DeviceNameText.Text = App.Services.DeviceName;
        DevicePortText.Text = App.Services.Coordinator.ListenPort > 0
            ? App.Services.Coordinator.ListenPort.ToString()
            : "—";
        var w = Entanglo.PInvoke.User32.GetSystemMetrics(Entanglo.PInvoke.User32.SM_CXSCREEN);
        var h = Entanglo.PInvoke.User32.GetSystemMetrics(Entanglo.PInvoke.User32.SM_CYSCREEN);
        ScreenSizeText.Text = $"{w} × {h}";
        DeviceIdText.Text   = App.Services.DeviceId;
        VersionText.Text    = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");
    }

    void ReceiveInputToggle_Toggled(object sender, RoutedEventArgs e)
    {
        App.Services.Settings.ReceiveRemoteInput = ReceiveInputToggle.IsOn;
        App.Services.Settings.Save();
    }

    void AutoDialToggle_Toggled(object sender, RoutedEventArgs e)
    {
        App.Services.Settings.AutoDialTrusted = AutoDialToggle.IsOn;
        App.Services.Settings.Save();
    }

    void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(App.Services.DeviceId);
        Clipboard.SetContent(pkg);
        MaintenanceHint.Text = "Device ID copied to clipboard.";
    }

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo");
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var log = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo", "entanglo.log");
        if (!System.IO.File.Exists(log))
        {
            MaintenanceHint.Text = "No log file yet.";
            return;
        }
        Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    void ResetDeviceId_Click(object sender, RoutedEventArgs e)
    {
        App.Services.ResetDeviceId();
        MaintenanceHint.Text = "Device ID will be regenerated at next launch. Restart the app to apply.";
    }

    void Quit_Click(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.QuitForUpdate();
}
