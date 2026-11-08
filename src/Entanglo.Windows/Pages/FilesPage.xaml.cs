using System.Collections.ObjectModel;
using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Entanglo.Windows.Pages;

public sealed partial class FilesPage : Page
{
    public sealed class TransferRow
    {
        public string Id { get; init; } = "";
        public string FileName { get; set; } = "";
        public string SubtitleText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string IconGlyph { get; set; } = "\uE898";
        public Brush TintBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        public double Progress { get; set; }
        public Visibility ProgressVisibility { get; set; } = Visibility.Visible;
    }

    readonly ObservableCollection<TransferRow> _rows = new();

    public FilesPage()
    {
        InitializeComponent();
        TransferList.ItemsSource = _rows;
        Loaded   += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    void Attach()
    {
        RefreshPeers();
        RefreshTransfers();
        App.Services.Coordinator.PeersChanged += OnPeersChanged;
        App.Services.FileTransfers.Changed    += OnTransfersChanged;
    }

    void Detach()
    {
        App.Services.Coordinator.PeersChanged -= OnPeersChanged;
        App.Services.FileTransfers.Changed    -= OnTransfersChanged;
    }

    void OnPeersChanged() => DispatcherQueue.TryEnqueue(RefreshPeers);
    void OnTransfersChanged() => DispatcherQueue.TryEnqueue(RefreshTransfers);

    void RefreshPeers()
    {
        var previous = PeerCombo.SelectedItem as ActivePeer;
        var peers = App.Services.Coordinator.ActivePeers.ToList();
        PeerCombo.ItemsSource = peers;
        PeerCombo.DisplayMemberPath = nameof(ActivePeer.DeviceName);
        if (previous != null)
        {
            var match = peers.FirstOrDefault(p => p.DeviceId == previous.DeviceId);
            if (match != null) PeerCombo.SelectedItem = match;
        }
        if (PeerCombo.SelectedItem == null && peers.Count == 1)
            PeerCombo.SelectedItem = peers[0];
    }

    void RefreshTransfers()
    {
        var snapshot = App.Services.FileTransfers.Snapshot();
        _rows.Clear();
        foreach (var t in snapshot)
            _rows.Add(BuildRow(t));
        EmptyLabel.Visibility  = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var downloadsDir = FileTransferService.DownloadsDir();
        var hasFiles = Directory.Exists(downloadsDir) && Directory.EnumerateFiles(downloadsDir).Any();
        OpenDownloadsButton.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenDownloadsButton_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start("explorer.exe", FileTransferService.DownloadsDir());

    static Brush BrushOr(string key, global::Windows.UI.Color fallback)
    {
        var res = App.Current.Resources;
        if (res.TryGetValue(key, out var value) && value is Brush b) return b;
        return new SolidColorBrush(fallback);
    }

    static TransferRow BuildRow(FileTransfer t)
    {
        var arrow = t.Direction == TransferDirection.Outgoing ? "→" : "←";
        var subtitle = $"{arrow} {t.PeerName} · {FormatBytes(t.SizeBytes)}";
        string status;
        string glyph;
        Brush tint;
        var progressVisible = Visibility.Visible;
        switch (t.Status)
        {
            case TransferStatus.Offered:
                status = "Offering…";
                glyph = "\uE898";
                tint  = BrushOr("EntangloAccentBrush", Microsoft.UI.Colors.SteelBlue);
                break;
            case TransferStatus.Active:
                status = $"{(int)(t.Progress * 100)}%";
                glyph = t.Direction == TransferDirection.Outgoing ? "\uE898" : "\uE896";
                tint  = BrushOr("EntangloAccentBrush", Microsoft.UI.Colors.SteelBlue);
                break;
            case TransferStatus.Completed:
                status = "Completed";
                glyph = "\uE73E";
                tint  = BrushOr("EntangloOkBrush", Microsoft.UI.Colors.SeaGreen);
                progressVisible = Visibility.Collapsed;
                break;
            case TransferStatus.Failed:
                status = t.ErrorMessage ?? "Failed";
                glyph = "\uEA39";
                tint  = BrushOr("EntangloErrorBrush", Microsoft.UI.Colors.IndianRed);
                progressVisible = Visibility.Collapsed;
                break;
            default:
                status = t.Status.ToString();
                glyph = "\uE711";
                tint  = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                progressVisible = Visibility.Collapsed;
                break;
        }
        return new TransferRow
        {
            Id = t.Id,
            FileName = t.FileName,
            SubtitleText = subtitle,
            StatusText = status,
            IconGlyph = glyph,
            TintBrush = tint,
            Progress = t.Progress,
            ProgressVisibility = progressVisible,
        };
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        if (PeerCombo.SelectedItem is not ActivePeer peer)
        {
            SendStatus.Text = "Pick a connected device first.";
            return;
        }
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var id = App.Services.FileTransfers.SendFile(file.Path, peer.DeviceId, peer.DeviceName);
        SendStatus.Text = $"Sending {file.Name} to {peer.DeviceName} (transfer {id[..8]}…)";
    }

    void ClearButton_Click(object sender, RoutedEventArgs e) =>
        App.Services.FileTransfers.ClearFinished();
}
