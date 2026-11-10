using System.Diagnostics;
using Entanglo.Core;
using Entanglo.Core.Input;
using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.Core.Updates;

namespace Entanglo.Windows;

/// <summary>
/// Singleton that owns the lifetime of all core services.
/// Accessed via App.Services from any page.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppSettings Settings { get; }
    public TrustStore TrustStore { get; }
    public ConnectionCoordinator Coordinator { get; }
    public DiscoveryService Discovery { get; }
    public InputInjectionService Injector { get; }
    public InputCaptureService Capture { get; }
    public EdgeSwitcherService EdgeSwitcher { get; }
    public UpdateService Updates { get; }
    public FileTransferService FileTransfers { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }

    readonly CancellationTokenSource _cts = new();

    public AppServices()
    {
        // Elevate process priority so background async tasks (heartbeat, input
        // receive) are not starved by Windows 11 power-throttling / EcoQoS when
        // the app window is hidden.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch { /* best-effort; may fail in some sandboxed environments */ }

        Settings    = AppSettings.Load();
        DeviceId    = GetOrCreateDeviceId();
        var sessionId  = Guid.NewGuid().ToString();
        DeviceName  = Environment.MachineName;

        TrustStore  = new TrustStore();
        TrustStore.Load();
        Coordinator   = new ConnectionCoordinator(DeviceId, sessionId, DeviceName, TrustStore);
        Discovery     = new DiscoveryService(DeviceName, 0, DeviceId); // port assigned after StartListeningAsync
        Injector      = new InputInjectionService();

        // Hook-based capture for the controller role (Windows → Mac).
        // The hook thread is self-contained and keeps Windows from disabling
        // the hook after ~5 s even when the main window is hidden.
        Capture      = new InputCaptureService();
        EdgeSwitcher = new EdgeSwitcherService(Capture, Coordinator, Settings);
        Capture.Start();

        FileTransfers = new FileTransferService();
        Coordinator.AttachFileTransfers(FileTransfers);
        Updates       = new UpdateService();
        Updates.WirePeerPush(Coordinator, FileTransfers);

        Coordinator.InputReceived += OnInputReceived;
    }

    public async Task StartAsync()
    {
        await Coordinator.StartListeningAsync(_cts.Token);
        Discovery.UpdatePort(Coordinator.ListenPort);
        await Discovery.StartAsync(_cts.Token);

        Discovery.PeerFound          += OnPeerFound;
        Coordinator.PeerDisconnected += OnPeerDropped;

        _ = Updates.RefreshAsync(_cts.Token);
        _ = ReconnectLoopAsync(_cts.Token);
    }

    void OnInputReceived(string _, InputEventMessage evt)
    {
        if (!Settings.ReceiveRemoteInput) return;
        try { Injector.Inject(evt); }
        catch (Exception ex) { AppLog.Error($"[Input] Inject failed: {ex.Message}"); }
    }

    // When a peer drops, kick off a discovery refresh after a short delay so
    // the link re-establishes within seconds rather than up to 30 s.
    void OnPeerDropped(string deviceId)
    {
        _ = TriggerReconnectSoonAsync();
    }

    async Task TriggerReconnectSoonAsync()
    {
        try
        {
            await Task.Delay(2000, _cts.Token);
            Discovery.Refresh();
        }
        catch (OperationCanceledException) { }
    }

    // Periodic safety net: if any trusted peer is missing, refresh discovery.
    // Fires every 8 s so gaps are never longer than ~10 s.
    async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(8_000, ct);
                var active = new HashSet<string>(Coordinator.ActivePeerIds, StringComparer.OrdinalIgnoreCase);
                bool anyMissing = TrustStore.Devices.Any(d => !active.Contains(d.DeviceId));
                if (anyMissing)
                    Discovery.Refresh();
            }
        }
        catch (OperationCanceledException) { }
    }

    void OnPeerFound(DiscoveredPeer peer)
    {
        if (!Settings.AutoDialTrusted) return;

        // Prefer UUID-based trust check; fall back to name matching for older peers
        // that do not advertise a Bonjour TXT id yet.
        var trustedById   = peer.DeviceId != null && TrustStore.IsTrusted(peer.DeviceId);
        var trustedByName = TrustStore.Devices.Any(d =>
            string.Equals(d.DeviceName, peer.InstanceName, StringComparison.OrdinalIgnoreCase));
        if (!trustedById && !trustedByName) return;

        // Skip if already connected by UUID
        if (peer.DeviceId != null && Coordinator.IsConnectedTo(peer.DeviceId)) return;
        // Skip if already connected by name
        if (Coordinator.ActivePeers.Any(p =>
            string.Equals(p.DeviceName, peer.InstanceName, StringComparison.OrdinalIgnoreCase)))
            return;

        _ = AutoDialAsync(peer);
    }

    async Task AutoDialAsync(DiscoveredPeer peer)
    {
        try
        {
            AppLog.Info($"[Auto] Re-dialing trusted peer {peer.InstanceName} @ {peer.Host}:{peer.Port}");
            await Coordinator.ConnectToPeerAsync(peer.Host, peer.Port, peer.DeviceId, peer.InstanceName, _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"[Auto] Re-dial to {peer.InstanceName} failed: {ex.Message}");
        }
    }

    public void ResetDeviceId()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo", "device-id.txt");
        if (File.Exists(path)) File.Delete(path);
    }

    public void Dispose()
    {
        Coordinator.InputReceived    -= OnInputReceived;
        Coordinator.PeerDisconnected -= OnPeerDropped;
        EdgeSwitcher.Dispose();
        Capture.Dispose();
        _cts.Cancel();
        Discovery.Dispose();
        Coordinator.Dispose();
    }

    static string GetOrCreateDeviceId()
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "device-id.txt");
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }
}
