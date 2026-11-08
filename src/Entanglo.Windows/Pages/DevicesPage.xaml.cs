using System.Collections.ObjectModel;
using System.ComponentModel;
using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Entanglo.Windows.Pages;

public sealed partial class DevicesPage : Page
{
    public ObservableCollection<PeerViewModel> Peers { get; } = [];
    public ObservableCollection<PairRequestViewModel> Pending { get; } = [];
    public ObservableCollection<TrustedDeviceViewModel> Trusted { get; } = [];

    readonly DiscoveryService _discovery;

    public DevicesPage()
    {
        InitializeComponent();
        DevicesList.ItemsSource = Peers;
        PendingList.ItemsSource = Pending;
        TrustedList.ItemsSource = Trusted;

        _discovery = App.Services.Discovery;
        _discovery.PeerFound += OnPeerFound;
        _discovery.PeerLost  += OnPeerLost;
        App.Services.Coordinator.PairRequestReceived += OnPairRequestReceived;
        App.Services.Coordinator.PeersChanged        += OnPeersChanged;

        AddKnownPeers();
        RefreshTrustedDevices();
        RefreshEmptyStates();

        Unloaded += (_, _) =>
        {
            _discovery.PeerFound -= OnPeerFound;
            _discovery.PeerLost  -= OnPeerLost;
            App.Services.Coordinator.PairRequestReceived -= OnPairRequestReceived;
            App.Services.Coordinator.PeersChanged        -= OnPeersChanged;
        };
    }

    void AddKnownPeers()
    {
        foreach (var peer in _discovery.CurrentPeers)
            OnPeerFound(peer);
    }

    void OnPeerFound(DiscoveredPeer peer)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Peers.Any(p => p.Host == peer.Host && p.Port == peer.Port)) return;
            var vm = new PeerViewModel
            {
                DeviceName = peer.InstanceName,
                Host = peer.Host,
                Port = peer.Port,
            };
            ApplyStatus(vm);
            Peers.Add(vm);
            StatusText.Text = Peers.Count.ToString();
            RefreshEmptyStates();
        });
    }

    void OnPeerLost(string id)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = Peers.FirstOrDefault(p => p.Host == id || p.DeviceName == id);
            if (vm != null) Peers.Remove(vm);
            StatusText.Text = Peers.Count.ToString();
            RefreshEmptyStates();
        });
    }

    void OnPairRequestReceived(string deviceId, string deviceName)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Pending.Any(p => p.DeviceId == deviceId)) return;
            Pending.Add(new PairRequestViewModel { DeviceId = deviceId, DeviceName = deviceName });
            RefreshEmptyStates();
        });
    }

    void OnPeersChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTrustedDevices();
            RefreshAllPeerStatuses();
            RefreshEmptyStates();
        });
    }

    void RefreshAllPeerStatuses()
    {
        foreach (var vm in Peers) ApplyStatus(vm);
    }

    void ApplyStatus(PeerViewModel vm)
    {
        var trusted = App.Services.TrustStore.Devices
            .Any(d => string.Equals(d.DeviceName, vm.DeviceName, StringComparison.OrdinalIgnoreCase));
        var connected = App.Services.Coordinator.ActivePeers
            .Any(p => string.Equals(p.DeviceName, vm.DeviceName, StringComparison.OrdinalIgnoreCase));

        if (connected)
        {
            vm.StatusLabel    = "Connected";
            vm.StatusGlyph    = "\uE73E";
            vm.ActionGlyph    = "\uE73E";
            vm.ActionTooltip  = "Connected";
            vm.CanConnect     = false;
            vm.GlowBrushKey   = "EntangloOkSoftBrush";
            vm.AccentBrushKey = "EntangloOkBrush";
        }
        else if (trusted)
        {
            vm.StatusLabel    = "Trusted";
            vm.StatusGlyph    = "\uE72E";
            vm.ActionGlyph    = "\uE71B";
            vm.ActionTooltip  = "Connect";
            vm.CanConnect     = true;
            vm.GlowBrushKey   = "EntangloOkSoftBrush";
            vm.AccentBrushKey = "EntangloOkBrush";
        }
        else
        {
            vm.StatusLabel    = "Discovered";
            vm.StatusGlyph    = "\uE701";
            vm.ActionGlyph    = "\uE71B";
            vm.ActionTooltip  = "Connect";
            vm.CanConnect     = true;
            vm.GlowBrushKey   = "EntangloAccentGlowBrush";
            vm.AccentBrushKey = "EntangloAccentBrush";
        }
        vm.Refresh();
    }

    void RefreshTrustedDevices()
    {
        Trusted.Clear();
        foreach (var device in App.Services.TrustStore.Devices)
            Trusted.Add(TrustedDeviceViewModel.From(device));
    }

    void RefreshEmptyStates()
    {
        DevicesEmpty.Visibility = Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesList.Visibility  = Peers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        PendingEmpty.Visibility = Pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingList.Visibility  = Pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PendingCountPill.Text   = Pending.Count.ToString();

        TrustedEmpty.Visibility = Trusted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrustedList.Visibility  = Trusted.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TrustedCountPill.Text   = Trusted.Count.ToString();
    }

    void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        Peers.Clear();
        StatusText.Text = "0";
        AddKnownPeers();
        _discovery.Refresh();
        RefreshEmptyStates();
    }

    void DevicesList_ItemClick(object sender, ItemClickEventArgs e) { }

    async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PeerViewModel peer) return;
        if (!peer.CanConnect) return;
        StatusText.Text = $"Connecting to {peer.DeviceName}...";
        AppLog.Info($"[UI] Connecting to {peer.DeviceName} at {peer.Host}:{peer.Port}");

        try
        {
            await App.Services.Coordinator.ConnectToPeerAsync(
                peer.Host,
                peer.Port,
                knownPeerId: null,
                knownPeerName: peer.DeviceName,
                CancellationToken.None);
            StatusText.Text = $"Opening link to {peer.DeviceName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection failed: {ex.Message}";
            AppLog.Error($"[UI] Connection to {peer.DeviceName} failed: {ex.Message}");
        }
    }

    async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        var host = ManualHost.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;
        if (!int.TryParse(ManualPort.Text.Trim(), out var port) || port <= 0) return;

        StatusText.Text = $"Connecting to {host}:{port}...";
        try
        {
            await App.Services.Coordinator.ConnectToPeerAsync(
                host, port, knownPeerId: null, knownPeerName: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
    }

    async void AllowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PairRequestViewModel req)
        {
            await App.Services.Coordinator.AcceptPairingAsync(req.DeviceId, true);
            Pending.Remove(req);
            RefreshTrustedDevices();
            RefreshAllPeerStatuses();
            RefreshEmptyStates();
        }
    }

    async void BlockButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PairRequestViewModel req)
        {
            await App.Services.Coordinator.AcceptPairingAsync(req.DeviceId, false);
            Pending.Remove(req);
            RefreshEmptyStates();
        }
    }

    void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TrustedDeviceViewModel device) return;
        App.Services.TrustStore.Revoke(device.DeviceId);
        App.Services.Coordinator.DisconnectPeer(device.DeviceId);
        RefreshTrustedDevices();
        RefreshAllPeerStatuses();
        RefreshEmptyStates();
    }
}

public class PeerViewModel : INotifyPropertyChanged
{
    public string DeviceName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }

    public string StatusLabel   { get; set; } = "Discovered";
    public string StatusGlyph   { get; set; } = "\uE701";   // Network signal
    public string ActionGlyph   { get; set; } = "\uE71B";   // Link
    public string ActionTooltip { get; set; } = "Connect";
    public bool   CanConnect    { get; set; } = true;
    public string GlowBrushKey   { get; set; } = "EntangloAccentGlowBrush";
    public string AccentBrushKey { get; set; } = "EntangloAccentBrush";

    public Brush GlowBrush =>
        (Brush)Application.Current.Resources[GlowBrushKey];
    public Brush AccentBrush =>
        (Brush)Application.Current.Resources[AccentBrushKey];

    public Visibility ConnectVisibility   => CanConnect ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectedVisibility => CanConnect ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionTooltip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnect)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlowBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectedVisibility)));
    }
}

public class PairRequestViewModel
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public class TrustedDeviceViewModel
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string PairedAt { get; set; } = "";

    public static TrustedDeviceViewModel From(TrustedDevice device) => new()
    {
        DeviceId = device.DeviceId,
        DeviceName = device.DeviceName,
        PairedAt = device.PairedAt.ToLocalTime().ToString("g"),
    };
}
