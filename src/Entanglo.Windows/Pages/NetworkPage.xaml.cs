using System.Collections.ObjectModel;
using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Entanglo.Windows.Pages;

public sealed partial class NetworkPage : Page
{
    public ObservableCollection<ActiveLinkViewModel> Links { get; } = [];

    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public NetworkPage()
    {
        InitializeComponent();
        LinksList.ItemsSource = Links;

        Refresh();
        App.Services.Coordinator.PeersChanged    += OnPeersChanged;
        App.Services.Discovery.PeerFound         += OnDiscoveryChanged;
        App.Services.Discovery.PeerLost          += OnDiscoveryChanged;
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            App.Services.Coordinator.PeersChanged -= OnPeersChanged;
            App.Services.Discovery.PeerFound      -= OnDiscoveryChanged;
            App.Services.Discovery.PeerLost       -= OnDiscoveryChanged;
        };
    }

    void OnPeersChanged() => DispatcherQueue.TryEnqueue(Refresh);
    void OnDiscoveryChanged(DiscoveredPeer _) => DispatcherQueue.TryEnqueue(Refresh);
    void OnDiscoveryChanged(string _) => DispatcherQueue.TryEnqueue(Refresh);

    void Refresh()
    {
        var peers   = App.Services.Coordinator.ActivePeers;
        var discovery = App.Services.Discovery.CurrentPeers;

        LinkedCountText.Text = peers.Count.ToString();
        LinkedSubText.Text   = peers.Count switch
        {
            0 => "No live links",
            1 => "1 live link",
            _ => $"{peers.Count} live links"
        };

        var rtts = peers.Where(p => p.LastRttMs.HasValue).Select(p => p.LastRttMs!.Value).ToArray();
        LatencyText.Text = rtts.Length == 0 ? "—" : $"{rtts.Min():F0} ms";

        DiscoveryCountText.Text = discovery.Count.ToString();
        DiscoverySubText.Text   = discovery.Count == 1
            ? "1 mDNS peer seen"
            : $"{discovery.Count} mDNS peers seen";

        ListenPortText.Text = App.Services.Coordinator.ListenPort > 0
            ? App.Services.Coordinator.ListenPort.ToString()
            : "—";

        Links.Clear();
        foreach (var p in peers) Links.Add(ActiveLinkViewModel.From(p));

        LinksEmpty.Visibility = Links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinksList.Visibility  = Links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

public class ActiveLinkViewModel
{
    public string DeviceName { get; set; } = "";
    public string PlatformVersion { get; set; } = "";
    public string RttLabel { get; set; } = "—";

    public static ActiveLinkViewModel From(ActivePeer p)
    {
        var platform = p.Platform ?? "?";
        var version  = p.AppVersion ?? "?";
        var rtt = p.LastRttMs.HasValue ? $"{p.LastRttMs.Value:F0} ms" : "—";
        return new ActiveLinkViewModel
        {
            DeviceName      = p.DeviceName,
            PlatformVersion = $"{platform} • v{version}",
            RttLabel        = rtt,
        };
    }
}
