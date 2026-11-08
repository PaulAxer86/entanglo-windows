using Entanglo.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Entanglo.Windows.Pages;

public sealed partial class UpdatesPage : Page
{
    bool _busy;

    public UpdatesPage()
    {
        InitializeComponent();
        Refresh();
        App.Services.Updates.Changed += OnUpdatesChanged;
        Unloaded += (_, _) => App.Services.Updates.Changed -= OnUpdatesChanged;
    }

    void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(Refresh);

    void Refresh()
    {
        var svc = App.Services.Updates;
        HeaderSubText.Text = $"You're running Entanglo {svc.CurrentVersion} on Windows";

        if (svc.AvailableUpdate is { } available)
        {
            AvailableCard.Visibility = Visibility.Visible;
            UpToDateCard.Visibility  = Visibility.Collapsed;
            AvailableTitle.Text      = $"Update available — {available.Version}";
            AvailableSubtitle.Text   = $"You're on {svc.CurrentVersion}. {available.Title}";
            AvailableHighlights.ItemsSource = available.Highlights;
            InstallButton.IsEnabled  = !_busy;
        }
        else
        {
            AvailableCard.Visibility = Visibility.Collapsed;
            UpToDateCard.Visibility  = Visibility.Visible;
            UpToDateTitle.Text       = $"You're on Entanglo {svc.CurrentVersion}";
        }

        if (svc.CurrentVersionNote is { } note)
        {
            WhatsNewCard.Visibility = Visibility.Visible;
            WhatsNewTitle.Text      = $"What's new in {note.Version}";
            WhatsNewDate.Text       = note.Date;
            WhatsNewSubtitle.Text   = note.Title;
            WhatsNewHighlights.ItemsSource = note.Highlights;
        }
        else
        {
            WhatsNewCard.Visibility = Visibility.Collapsed;
        }

        var older = svc.ReleaseNotes
            .Where(n => n.Version != svc.CurrentVersion)
            .ToList();
        if (older.Count > 0)
        {
            ChangelogCard.Visibility = Visibility.Visible;
            ChangelogList.ItemsSource = older;
        }
        else
        {
            ChangelogCard.Visibility = Visibility.Collapsed;
        }
    }

    async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        StatusFooter.Text = "Checking entanglo.netlify.app…";
        CheckButton.IsEnabled = false;
        try
        {
            await App.Services.Updates.RefreshAsync();
            StatusFooter.Text = "";
        }
        catch (Exception ex)
        {
            StatusFooter.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || App.Services.Updates.AvailableUpdate == null) return;
        _busy = true;
        InstallButton.IsEnabled = false;
        InstallButton.Content   = "Preparing…";
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value      = 0;

        var peers = App.Services.Coordinator.ActivePeerIds.Count;
        StatusFooter.Text = peers > 0
            ? $"Pushing to {peers} peer(s), then installing — Entanglo will quit and relaunch."
            : "Launching upgrade — Entanglo will quit and relaunch.";

        var progress = new Progress<double>(v =>
            DispatcherQueue.TryEnqueue(() => DownloadProgress.Value = v));

        try
        {
            await App.Services.Updates.BeginInstallAsync(
                () => DispatcherQueue.TryEnqueue(() => (Application.Current as App)?.QuitForUpdate()),
                downloadProgress: progress);
        }
        catch (Exception ex)
        {
            StatusFooter.Text = $"Install failed: {ex.Message}";
            InstallButton.Content   = "Install…";
            InstallButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }
}
