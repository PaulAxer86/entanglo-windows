using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Entanglo.Core.Logging;
using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Net;

public enum PairingState { Unknown, Requested, Trusted, Blocked }

// Mutable class (not record) so the read/heartbeat loops keep working with
// the same reference across a Hello rekey — record `with { ... }` would put
// a fresh clone in the dictionary while the loops kept mutating the stale
// one, which is what stopped the Dashboard latency from ever updating.
public sealed class PeerSession
{
    public string DeviceId { get; set; }
    public string DeviceName { get; set; }
    public NetworkTransport Transport { get; }
    public CancellationTokenSource Cts { get; }
    public DateTimeOffset LastInboundAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;
    public double? LastRttMs { get; set; }
    public double? PeerLastSentAtMs { get; set; }
    public string? Platform { get; set; }
    public string? AppVersion { get; set; }
    public int?    ScreenWidth  { get; set; }
    public int?    ScreenHeight { get; set; }

    public PeerSession(string deviceId, string deviceName, NetworkTransport transport, CancellationTokenSource cts)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Transport = transport;
        Cts = cts;
    }
}

public record ActivePeer(
    string DeviceId,
    string DeviceName,
    string? Platform,
    string? AppVersion,
    double? LastRttMs,
    bool Trusted,
    DateTimeOffset OpenedAt,
    int? ScreenWidth  = null,
    int? ScreenHeight = null)
{
    /// <summary>UI-ready RTT string ("42 ms" or "—") — avoids a XAML converter.</summary>
    public string RttDisplay => LastRttMs.HasValue ? $"{LastRttMs.Value:F0} ms" : "—";
}

/// <summary>
/// Owns the TCP listener, manages per-peer sessions, drives heartbeats,
/// dispatches incoming messages to registered handlers.
/// </summary>
public sealed class ConnectionCoordinator : IDisposable
{
    const int HeartbeatIntervalMs = 250;   // faster heartbeat → RTT display reflects actual LAN latency instead of the heartbeat-phase artefact (~900 ms at 1 s interval)
    const int HeartbeatTimeoutMs  = 15000; // generous: Windows power-throttling can delay the thread pool by several seconds when the app is backgrounded

    readonly string _deviceId;
    readonly string _sessionId;
    readonly string _deviceName;
    readonly TrustStore _trustStore;

    TcpListener? _listener;
    int _listenPort;
    long _hbSequence;
    long _loggedHeartbeatSends;

    readonly ConcurrentDictionary<string, PeerSession> _peers = new();
    FileTransferService? _fileTransfers;

    // Events the UI layer wires up
    public event Action<string, string>? PairRequestReceived;   // (deviceId, deviceName)
    public event Action<string>? PeerConnected;                 // deviceId
    public event Action<string>? PeerDisconnected;              // deviceId
    public event Action? PeersChanged;                          // any session add/update/remove
    public event Action<string, InputEventMessage>? InputReceived;
    public event Action<string, string>? ClipboardReceived;     // (deviceId, text)

    public int ListenPort => _listenPort;
    public IReadOnlyCollection<string> ActivePeerIds => _peers.Keys.ToArray();

    public IReadOnlyCollection<ActivePeer> ActivePeers =>
        _peers.Values.Select(s => new ActivePeer(
            s.DeviceId,
            s.DeviceName,
            s.Platform,
            s.AppVersion,
            s.LastRttMs,
            _trustStore.IsTrusted(s.DeviceId),
            s.OpenedAt,
            s.ScreenWidth,
            s.ScreenHeight)).ToArray();

    public bool IsConnectedTo(string deviceId) => _peers.ContainsKey(deviceId);

    /// <summary>Peer display name for a device id — safe to call for unknown ids.</summary>
    public string PeerDisplayName(string deviceId) =>
        _peers.TryGetValue(deviceId, out var s) ? s.DeviceName : deviceId;

    /// <summary>Wires the file transfer pipeline into the connection layer.</summary>
    public void AttachFileTransfers(FileTransferService svc)
    {
        _fileTransfers = svc;
        svc.SendToPeer = (peerId, type, payloadJson) =>
        {
            if (!_peers.TryGetValue(peerId, out var session)) return;
            var frame = MessageCodec.EncodeRaw(type, _deviceId, _sessionId, payloadJson);
            _ = session.Transport.SendFrameAsync(frame, session.Cts.Token);
        };
    }

    public ConnectionCoordinator(string deviceId, string sessionId, string deviceName, TrustStore trustStore)
    {
        _deviceId   = deviceId;
        _sessionId  = sessionId;
        _deviceName = deviceName;
        _trustStore = trustStore;
    }

    public async Task StartListeningAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        _listenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AppLog.Info($"[Net] Listening on port {_listenPort}");

        _ = AcceptLoopAsync(ct);
        await Task.CompletedTask;
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                AppLog.Info($"[Net] Accepted TCP from {client.Client.RemoteEndPoint}");
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppLog.Error($"[Net] Accept error: {ex.Message}"); }
        }
    }

    public Task ConnectToPeerAsync(string host, int port, CancellationToken ct) =>
        ConnectToPeerAsync(host, port, knownPeerId: null, knownPeerName: null, ct);

    public async Task ConnectToPeerAsync(
        string host,
        int port,
        string? knownPeerId,
        string? knownPeerName,
        CancellationToken ct)
    {
        // De-dupe: if we already have a session whose name or id matches,
        // skip the dial so we don't pile up half-open sessions.
        if (knownPeerId != null && _peers.ContainsKey(knownPeerId))
        {
            AppLog.Info($"[Net] Skipping dial — already connected to {knownPeerId}");
            return;
        }
        if (knownPeerName != null &&
            _peers.Values.Any(s => string.Equals(s.DeviceName, knownPeerName, StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Info($"[Net] Skipping dial — already connected to peer named {knownPeerName}");
            return;
        }

        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        AppLog.Info($"[Net] TCP connected to {host}:{port} from {client.Client.LocalEndPoint}");
        _ = HandleConnectionAsync(client, ct, knownPeerId, knownPeerName);
    }

    async Task HandleConnectionAsync(
        TcpClient client,
        CancellationToken parentCt,
        string? knownPeerId = null,
        string? knownPeerName = null)
    {
        // Mouse / keyboard frames are tiny — disable Nagle so they hit the wire
        // immediately instead of being coalesced (which produced the visible
        // jitter on the receiver side).
        try { client.NoDelay = true; } catch { /* socket may have just closed */ }

        // TCP keepalive: OS probes after 5 s of idle, every 2 s, up to 5 retries.
        // Detects silent network drops (WiFi roam, switch reboot) without waiting
        // for our 15 s application-level heartbeat timeout.
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime,        5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval,   2);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch { /* not fatal — falls back to application-level heartbeat */ }

        var transport = new NetworkTransport(client);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        // Provisional id derived from the wire; replaced once Hello lands.
        string provisionalId = knownPeerId
            ?? client.Client.RemoteEndPoint?.ToString()
            ?? Guid.NewGuid().ToString();
        string peerName = knownPeerName ?? provisionalId;

        var session = new PeerSession(provisionalId, peerName, transport, cts);
        _peers[provisionalId] = session;
        PeerConnected?.Invoke(provisionalId);
        PeersChanged?.Invoke();
        AppLog.Info($"[Net] Link opened to {peerName} ({provisionalId})");

        try
        {
            var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
            var sw = User32.GetSystemMetrics(User32.SM_CXSCREEN);
            var sh = User32.GetSystemMetrics(User32.SM_CYSCREEN);
            var hello = new HelloPayload(
                _deviceName, "PC", appVersion, ["controller", "receiver"], "Windows",
                sw > 0 ? sw : null, sh > 0 ? sh : null);
            AppLog.Info($"[Net] TX hello to {peerName}");
            await transport.SendFrameAsync(
                MessageCodec.Encode(MessageType.Hello, _deviceId, _sessionId, hello), cts.Token);

            _ = HeartbeatLoopAsync(session, cts.Token);
            _ = WatchdogLoopAsync(session, cts.Token);
            await ReadLoopAsync(session, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"[Net] Peer {session.DeviceId} error: {ex.Message}");
        }
        finally
        {
            // After a Hello rekey the dictionary key may no longer be the
            // provisional id we started with — find the live session by
            // transport reference so we don't leave an orphan entry
            // pointing at a now-dead transport (which is what blocked
            // iMac1 ↔ Windows reconnection after a rekey in 0.1.41).
            var live = _peers.FirstOrDefault(kv =>
                ReferenceEquals(kv.Value.Transport, transport));
            var closedId = live.Key ?? session.DeviceId;
            var closedName = live.Value?.DeviceName ?? session.DeviceName;
            if (live.Key != null) _peers.TryRemove(live.Key, out _);
            else _peers.TryRemove(session.DeviceId, out _);
            cts.Cancel();
            transport.Dispose();
            PeerDisconnected?.Invoke(closedId);
            PeersChanged?.Invoke();
            AppLog.Info($"[Net] Link closed: {closedName} ({closedId})");
        }
    }

    async Task ReadLoopAsync(PeerSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await session.Transport.ReadFrameAsync(ct);
            var env   = MessageCodec.DecodeEnvelope(frame);
            session.LastInboundAt = DateTimeOffset.UtcNow;
            if (env.MessageType != MessageType.Heartbeat)
                AppLog.Info($"[Net] RX {env.MessageType} from {env.SenderDeviceId} ({frame.Length} B)");
            DispatchMessage(session, env);
        }
    }

    void DispatchMessage(PeerSession session, EntangloMessage env)
    {
        switch (env.MessageType)
        {
            case MessageType.Hello:
            {
                var h = MessageCodec.DecodePayload<HelloPayload>(env);
                AppLog.Info($"[Net] Hello from {h.DeviceName} ({env.SenderDeviceId}, {h.Platform ?? "?"}, v{h.AppVersion})");
                RekeySession(session, env.SenderDeviceId, h.DeviceName, h.Platform, h.AppVersion);
                if (h.ScreenWidth > 0 && h.ScreenHeight > 0)
                {
                    session.ScreenWidth  = h.ScreenWidth;
                    session.ScreenHeight = h.ScreenHeight;
                }
                // Trust-on-first-use: the macOS reference peer does not send
                // pairRequest/pairResponse on the wire — pairing is local on
                // its side. To keep the Windows trust store aligned, we trust
                // any peer that completes a Hello handshake on the LAN.
                if (!_trustStore.IsTrusted(env.SenderDeviceId))
                {
                    // Migrate any name-keyed trust entry for this device to its stable UUID
                    MigrateTrustByName(env.SenderDeviceId, h.DeviceName);
                    if (!_trustStore.IsTrusted(env.SenderDeviceId))
                    {
                        _trustStore.Trust(env.SenderDeviceId, h.DeviceName);
                        AppLog.Info($"[Trust] TOFU-trusted {h.DeviceName} ({env.SenderDeviceId})");
                    }
                }
                PeersChanged?.Invoke();
                break;
            }
            case MessageType.PairResponse:
            {
                var r = MessageCodec.DecodePayload<PairResponsePayload>(env);
                if (r.Accepted) _trustStore.Trust(session.DeviceId, session.DeviceName);
                PeersChanged?.Invoke();
                break;
            }
            case MessageType.PairRequest:
            {
                var r = MessageCodec.DecodePayload<PairRequestPayload>(env);
                PairRequestReceived?.Invoke(session.DeviceId, r.RequesterDeviceName);
                break;
            }
            case MessageType.InputEvent:
                InputReceived?.Invoke(session.DeviceId,
                    MessageCodec.DecodePayload<InputEventMessage>(env));
                break;
            case MessageType.ClipboardText:
                ClipboardReceived?.Invoke(session.DeviceId,
                    MessageCodec.DecodePayload<ClipboardTextPayload>(env).Text);
                break;
            case MessageType.FileOffer:
                if (_fileTransfers != null)
                {
                    var offer = MessageCodec.DecodePayload<FileOfferPayload>(env);
                    _fileTransfers.HandleOffer(offer, session.DeviceId, session.DeviceName);
                }
                break;
            case MessageType.FileChunk:
                if (_fileTransfers != null)
                {
                    var chunk = MessageCodec.DecodePayload<FileChunkPayload>(env);
                    _fileTransfers.HandleChunk(chunk, session.DeviceId);
                }
                break;
            case MessageType.FileAck:
                if (_fileTransfers != null)
                {
                    var ack = MessageCodec.DecodePayload<FileAckPayload>(env);
                    _fileTransfers.HandleAck(ack);
                }
                break;
            case MessageType.Heartbeat:
            {
                var hb = MessageCodec.DecodePayload<HeartbeatMessage>(env);
                if (hb.EchoSentAtMs is { } echo)
                {
                    var now = (double)System.Diagnostics.Stopwatch.GetTimestamp()
                              / System.Diagnostics.Stopwatch.Frequency * 1000.0;
                    var rtt = now - echo;
                    if (rtt >= 0 && rtt < 60_000)
                        session.LastRttMs = rtt;
                    // PeersChanged not fired here: at 250 ms heartbeat interval that
                    // would push 4 UI refreshes/second.  DashboardPage has its own
                    // 1.5 s timer that picks up the updated LastRttMs from ActivePeers.
                }
                if (hb.SentAtMs is { } s) session.PeerLastSentAtMs = s;
                break;
            }
        }
    }

    void MigrateTrustByName(string uuid, string displayName)
    {
        // If a trust record exists keyed by the display name (not a UUID — legacy behavior
        // from very old installs or cross-platform imports), carry it over to the stable UUID.
        var nameKeyed = _trustStore.Devices.FirstOrDefault(d =>
            !Guid.TryParse(d.DeviceId, out _) &&
            string.Equals(d.DeviceName, displayName, StringComparison.OrdinalIgnoreCase));
        if (nameKeyed == null) return;
        AppLog.Info($"[Trust] Migrating name-keyed trust '{nameKeyed.DeviceId}' → {uuid}");
        _trustStore.Trust(uuid, displayName);
        _trustStore.Revoke(nameKeyed.DeviceId);
    }

    void RekeySession(PeerSession session, string newDeviceId, string newDeviceName, string? platform, string? appVersion)
    {
        // Mutate in place so ReadLoop / HeartbeatLoop keep working with the
        // same PeerSession reference. Only the dictionary key changes when
        // the peer switches identity.
        session.Platform = platform;
        session.AppVersion = appVersion;
        session.DeviceName = newDeviceName;

        if (string.Equals(session.DeviceId, newDeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        // Different key: drop any older session for the real id (newer wins).
        if (_peers.TryRemove(newDeviceId, out var older) && !ReferenceEquals(older, session))
        {
            AppLog.Info($"[Net] Replacing older session for {newDeviceName} ({newDeviceId})");
            older.Cts.Cancel();
            older.Transport.Dispose();
        }

        var oldId = session.DeviceId;
        _peers.TryRemove(oldId, out _);
        session.DeviceId = newDeviceId;
        _peers[newDeviceId] = session;
        PeerDisconnected?.Invoke(oldId);
        PeerConnected?.Invoke(newDeviceId);
    }

    async Task HeartbeatLoopAsync(PeerSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatIntervalMs, ct);
            var nowMs = (double)System.Diagnostics.Stopwatch.GetTimestamp()
                        / System.Diagnostics.Stopwatch.Frequency * 1000.0;
            var hb = new HeartbeatMessage(
                Sequence: Interlocked.Increment(ref _hbSequence),
                LastRttMs: session.LastRttMs,
                SentAtMs: nowMs,
                EchoSentAtMs: session.PeerLastSentAtMs);
            try
            {
                var logOrdinal = Interlocked.Increment(ref _loggedHeartbeatSends);
                if (logOrdinal <= 10 || logOrdinal % 60 == 0)
                    AppLog.Info($"[Net] TX heartbeat #{hb.Sequence} to {session.DeviceName}");
                await session.Transport.SendFrameAsync(
                    MessageCodec.Encode(MessageType.Heartbeat, _deviceId, _sessionId, hb), ct);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Net] Heartbeat send failed to {session.DeviceName}: {ex.Message}");
                break;
            }
        }
    }

    async Task WatchdogLoopAsync(PeerSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var silenceMs = (DateTimeOffset.UtcNow - session.LastInboundAt).TotalMilliseconds;
                if (silenceMs > HeartbeatTimeoutMs)
                {
                    AppLog.Warning($"[Net] Heartbeat timeout from {session.DeviceName} ({silenceMs:F0}ms) — closing link");
                    session.Cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task AcceptPairingAsync(string deviceId, bool accepted)
    {
        if (!_peers.TryGetValue(deviceId, out var session)) return;
        var resp = new PairResponsePayload(
            Accepted: accepted,
            TrustedDeviceId: accepted ? deviceId : null,
            RejectionReason: accepted ? null : "rejected_by_user");
        if (accepted) _trustStore.Trust(deviceId, session.DeviceName);
        await session.Transport.SendFrameAsync(
            MessageCodec.Encode(MessageType.PairResponse, _deviceId, _sessionId, resp),
            session.Cts.Token);
        PeersChanged?.Invoke();
    }

    public async Task SendInputAsync(string peerId, InputEventMessage evt)
    {
        if (!_peers.TryGetValue(peerId, out var session)) return;
        await session.Transport.SendFrameAsync(
            MessageCodec.Encode(MessageType.InputEvent, _deviceId, _sessionId, evt),
            session.Cts.Token);
    }

    public async Task BroadcastClipboardAsync(string text, CancellationToken ct = default)
    {
        var payload = new ClipboardTextPayload(text);
        foreach (var session in _peers.Values)
        {
            try
            {
                await session.Transport.SendFrameAsync(
                    MessageCodec.Encode(MessageType.ClipboardText, _deviceId, _sessionId, payload),
                    ct == default ? session.Cts.Token : ct);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"[Net] Clipboard broadcast to {session.DeviceName} failed: {ex.Message}");
            }
        }
    }

    public void DisconnectPeer(string deviceId)
    {
        if (_peers.TryGetValue(deviceId, out var session))
        {
            AppLog.Info($"[Net] Closing link to {session.DeviceName} on request");
            session.Cts.Cancel();
        }
    }

    public void Dispose()
    {
        foreach (var s in _peers.Values) s.Cts.Cancel();
        _listener?.Stop();
    }
}
