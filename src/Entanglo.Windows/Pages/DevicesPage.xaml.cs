using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Entanglo.Windows.Pages;

public sealed partial class DevicesPage : Page
{
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public ObservableCollection<PairRequestViewModel> Pending { get; } = [];

    readonly DiscoveryService _discovery;

    public DevicesPage()
    {
        InitializeComponent();
        DevicesList.ItemsSource = Devices;
        PendingList.ItemsSource = Pending;

        _discovery = App.Services.Discovery;
        _discovery.PeerFound += OnPeerFoundOrLost;
        _discovery.PeerLost  += OnPeerNameLost;
        App.Services.Coordinator.PairRequestReceived += OnPairRequestReceived;
        App.Services.Coordinator.PeersChanged        += OnPeersChanged;

        RebuildDevices();

        Unloaded += (_, _) =>
        {
            _discovery.PeerFound -= OnPeerFoundOrLost;
            _discovery.PeerLost  -= OnPeerNameLost;
            App.Services.Coordinator.PairRequestReceived -= OnPairRequestReceived;
            App.Services.Coordinator.PeersChanged        -= OnPeersChanged;
        };
    }

    // DiscoveryService event shims → marshal to UI thread

    void OnPeerFoundOrLost(DiscoveredPeer _) =>
        DispatcherQueue.TryEnqueue(RebuildDevices);

    void OnPeerNameLost(string _) =>
        DispatcherQueue.TryEnqueue(RebuildDevices);

    void OnPeersChanged() =>
        DispatcherQueue.TryEnqueue(RebuildDevices);

    void OnPairRequestReceived(string deviceId, string deviceName)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Pending.Any(p => p.DeviceId == deviceId)) return;
            Pending.Add(new PairRequestViewModel { DeviceId = deviceId, DeviceName = deviceName });
            RefreshPendingUI();
        });
    }

    // Builds (or rebuilds) the unified device list from three sources:
    // 1. Trust store  — trusted devices, may be offline
    // 2. Active peers — connected, have platform/RTT
    // 3. Discovery    — currently visible on LAN, have host/port
    void RebuildDevices()
    {
        var map = new Dictionary<string, DeviceRowViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in App.Services.TrustStore.Devices)
            map[d.DeviceId] = new DeviceRowViewModel
            {
                DeviceId   = d.DeviceId,
                DeviceName = d.DeviceName,
                IsTrusted  = true,
            };

        foreach (var p in App.Services.Coordinator.ActivePeers)
        {
            if (!map.TryGetValue(p.DeviceId, out var row))
                map[p.DeviceId] = row = new DeviceRowViewModel { DeviceId = p.DeviceId };
            row.DeviceName  = p.DeviceName;
            row.Platform    = p.Platform ?? "";
            row.IsConnected = true;
            row.IsTrusted   = p.Trusted;
            row.RttMs       = p.LastRttMs;
        }

        foreach (var peer in _discovery.CurrentPeers)
        {
            var existing = map.Values.FirstOrDefault(r =>
                string.Equals(r.DeviceName, peer.InstanceName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new DeviceRowViewModel { DeviceName = peer.InstanceName };
                map["_disc_" + peer.InstanceName] = existing;
            }
            existing.Host = peer.Host;
            existing.Port = peer.Port;
        }

        Devices.Clear();
        foreach (var row in map.Values
            .OrderByDescending(r => r.IsConnected)
            .ThenByDescending(r => r.IsTrusted)
            .ThenBy(r => r.DeviceName))
            Devices.Add(row);

        DeviceCountPill.Text    = Devices.Count.ToString();
        DevicesEmpty.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesList.Visibility  = Devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshPendingUI()
    {
        PendingCountPill.Text   = Pending.Count.ToString();
        PendingEmpty.Visibility = Pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingList.Visibility  = Pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _discovery.Refresh();
        RebuildDevices();
    }

    async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceRowViewModel row || !row.CanConnect) return;
        AppLog.Info($"[UI] Connecting to {row.DeviceName} at {row.Host}:{row.Port}");
        try
        {
            await App.Services.Coordinator.ConnectToPeerAsync(
                row.Host!, row.Port, knownPeerId: null, knownPeerName: row.DeviceName,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[UI] Connection to {row.DeviceName} failed: {ex.Message}");
        }
    }

    void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not DeviceRowViewModel row) return;
        App.Services.TrustStore.Revoke(row.DeviceId);
        App.Services.Coordinator.DisconnectPeer(row.DeviceId);
        RebuildDevices();
    }

    async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        var host = ManualHost.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;
        if (!int.TryParse(ManualPort.Text.Trim(), out var port) || port <= 0) return;
        try
        {
            await App.Services.Coordinator.ConnectToPeerAsync(
                host, port, knownPeerId: null, knownPeerName: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[UI] Manual connect to {host}:{port} failed: {ex.Message}");
        }
    }

    async void AllowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PairRequestViewModel req) return;
        await App.Services.Coordinator.AcceptPairingAsync(req.DeviceId, true);
        Pending.Remove(req);
        RefreshPendingUI();
        RebuildDevices();
    }

    async void BlockButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PairRequestViewModel req) return;
        await App.Services.Coordinator.AcceptPairingAsync(req.DeviceId, false);
        Pending.Remove(req);
        RefreshPendingUI();
    }
}

// ── View models ───────────────────────────────────────────────────────────────

public class DeviceRowViewModel : INotifyPropertyChanged
{
    string _deviceId   = "";
    string _deviceName = "";
    string _platform   = "";
    bool   _isTrusted;
    bool   _isConnected;
    double? _rttMs;
    string? _host;
    int    _port;

    public string  DeviceId    { get => _deviceId;    set => Set(ref _deviceId,    value); }
    public string  DeviceName  { get => _deviceName;  set => Set(ref _deviceName,  value); }
    public string  Platform    { get => _platform;    set => Set(ref _platform,    value); }
    public bool    IsTrusted   { get => _isTrusted;   set => Set(ref _isTrusted,   value); }
    public bool    IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }
    public double? RttMs       { get => _rttMs;       set => Set(ref _rttMs,       value); }
    public string? Host        { get => _host;        set => Set(ref _host,        value); }
    public int     Port        { get => _port;        set => Set(ref _port,        value); }

    // "Linux · Trusted", "macOS · Connected", "Discovered", …
    public string Subtitle
    {
        get
        {
            var status = IsConnected ? "Connected" : IsTrusted ? "Trusted" : "Discovered";
            return string.IsNullOrEmpty(Platform) ? status : $"{Platform} · {status}";
        }
    }

    public string RttDisplay =>
        IsConnected && RttMs.HasValue ? $"{RttMs.Value:F0} ms" : "—";

    public Brush TrustBrush =>
        Brush(IsTrusted ? "EntangloOkBrush" : "EntangloTextMutedBrush");

    public Brush ConnectionBrush =>
        Brush(IsConnected ? "EntangloAccentBrush" : "EntangloTextMutedBrush");

    public Brush RttBrush =>
        Brush(IsConnected ? "EntangloTextPrimaryBrush" : "EntangloTextMutedBrush");

    public double FeatureOpacity => IsConnected ? 0.6 : 0.2;

    public bool       CanConnect      => !IsConnected && Host != null;
    public Visibility ConnectVisibility => CanConnect  ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RevokeVisibility  => IsTrusted   ? Visibility.Visible : Visibility.Collapsed;

    // ─────────────────────────────────────────────────────────────────────────

    static Brush Brush(string resourceKey) =>
        (Brush)Application.Current.Resources[resourceKey];

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Fire(name!);
        // Fire all computed properties so bindings refresh automatically
        Fire(nameof(Subtitle));     Fire(nameof(RttDisplay));
        Fire(nameof(TrustBrush));   Fire(nameof(ConnectionBrush)); Fire(nameof(RttBrush));
        Fire(nameof(FeatureOpacity));
        Fire(nameof(CanConnect));   Fire(nameof(ConnectVisibility)); Fire(nameof(RevokeVisibility));
    }

    void Fire(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class PairRequestViewModel
{
    public string DeviceId   { get; set; } = "";
    public string DeviceName { get; set; } = "";
}
