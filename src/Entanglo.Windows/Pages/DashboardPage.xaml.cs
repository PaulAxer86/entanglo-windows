using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Entanglo.Windows.Pages;

public sealed partial class DashboardPage : Page
{
    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public DashboardPage()
    {
        InitializeComponent();

        DeviceNameText.Text = App.Services.DeviceName;
        DeviceIdText.Text   = App.Services.DeviceId;
        RefreshAll();

        App.Services.Coordinator.PeersChanged    += OnPeersChanged;
        App.Services.Coordinator.PeerConnected   += OnPeerEvent;
        App.Services.Coordinator.PeerDisconnected += OnPeerEvent;
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            App.Services.Coordinator.PeersChanged    -= OnPeersChanged;
            App.Services.Coordinator.PeerConnected   -= OnPeerEvent;
            App.Services.Coordinator.PeerDisconnected -= OnPeerEvent;
        };
    }

    void OnPeersChanged() => DispatcherQueue.TryEnqueue(RefreshAll);
    void OnPeerEvent(string _) => DispatcherQueue.TryEnqueue(RefreshAll);

    void RefreshAll()
    {
        var peers = App.Services.Coordinator.ActivePeers;
        var trusted = App.Services.TrustStore.Devices;

        DevicePortText.Text = App.Services.Coordinator.ListenPort > 0
            ? App.Services.Coordinator.ListenPort.ToString()
            : "—";

        ConnectedCountText.Text = peers.Count.ToString();
        TrustedCountText.Text   = trusted.Count.ToString();

        var bestRtt = peers
            .Where(p => p.LastRttMs.HasValue)
            .Select(p => p.LastRttMs!.Value)
            .DefaultIfEmpty(double.NaN)
            .Min();
        LatencyText.Text = double.IsNaN(bestRtt) ? "—" : $"{bestRtt:F0} ms";

        ActivePeersCountText.Text = peers.Count.ToString();

        var linked = peers.Count > 0;
        LinkTitleText.Text    = linked ? "Linked" : "Waiting";
        LinkSubtitleText.Text = linked
            ? $"Streaming events to {peers.Count} trusted peer{(peers.Count == 1 ? "" : "s")}"
            : "Looking for trusted peers on the local network";
        LinkPillIcon.Glyph = linked ? "\uE73E" : "\uE916";
        HeroCard.BorderBrush = new SolidColorBrush(linked
            ? global::Microsoft.UI.Colors.LightGreen
            : global::Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        InputStatusText.Text     = linked ? "Linked" : "Idle";
        PairingSummaryText.Text  = linked
            ? $"{peers.Count} connected peer{(peers.Count == 1 ? "" : "s")}"
            : "No connected peers yet";

        PeersList.ItemsSource = peers;
        PeersEmptyState.Visibility = peers.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        PeersList.Visibility = peers.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    }
}
