using System.Net;
using Entanglo.Core.Logging;
using Makaretu.Dns;

namespace Entanglo.Core.Net;

public record DiscoveredPeer(string InstanceName, string Host, int Port);

/// <summary>
/// mDNS advertise + browse for _entanglo._tcp using Makaretu multicast DNS.
/// Compatible with Bonjour on macOS.
/// SRV and A records often arrive in separate messages, so we cache them
/// and emit PeerFound once both host and port are known.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    const string ServiceType = "_entanglo._tcp";
    const string ServiceTypeLocal = "_entanglo._tcp.local";
    const string ServiceTypeFqdnPart = "._entanglo._tcp.";

    readonly string _deviceName;
    int _port;

    MulticastService? _mdns;
    ServiceDiscovery? _sd;
    ServiceProfile?   _profile;

    readonly Dictionary<string, (string host, int port)> _srvCache = new();
    readonly Dictionary<string, string>                  _aCache   = new();
    readonly HashSet<string> _reported = new();
    readonly Dictionary<string, DiscoveredPeer> _knownPeers = new(StringComparer.OrdinalIgnoreCase);

    public event Action<DiscoveredPeer>? PeerFound;
    public event Action<string>?         PeerLost;

    public IReadOnlyCollection<DiscoveredPeer> CurrentPeers => _knownPeers.Values.ToArray();

    public DiscoveryService(string deviceName, int port)
    {
        _deviceName = deviceName;
        _port = port;
    }

    public void UpdatePort(int port) => _port = port;

    public Task StartAsync(CancellationToken ct = default)
    {
        _mdns = new MulticastService();
        _mdns.UseIpv4 = true;
        _mdns.UseIpv6 = false;

        _sd = new ServiceDiscovery(_mdns);
        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _sd.ServiceInstanceShutdown += OnServiceInstanceShutdown;
        _mdns.AnswerReceived += OnAnswerReceived;

        _profile = new ServiceProfile(_deviceName, ServiceType, (ushort)_port);
        _sd.Advertise(_profile);

        _mdns.Start();
        Refresh();
        _ = PeriodicQueryAsync(ct);

        return Task.CompletedTask;
    }

    async Task PeriodicQueryAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                Refresh();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Refresh()
    {
        if (_sd == null || _mdns == null) return;

        AppLog.Info("[mDNS] Querying Entanglo peers");
        _sd.QueryServiceInstances(ServiceType);
        _sd.QueryServiceInstances(ServiceTypeLocal);
        _sd.QueryUnicastServiceInstances(ServiceTypeLocal);
        _mdns.SendQuery(ServiceTypeLocal, DnsClass.IN, DnsType.PTR);
    }

    void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        var instance = NormalizeDnsName(e.ServiceInstanceName.ToString());
        if (!IsEntangloServiceName(instance)) return;

        AppLog.Info($"[mDNS] Instance -> {instance}");
        _mdns?.SendQuery(instance, DnsClass.IN, DnsType.SRV);
        _mdns?.SendQuery(instance, DnsClass.IN, DnsType.TXT);
    }

    void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var instance = NormalizeDnsName(e.ServiceInstanceName.ToString());
        if (!IsEntangloServiceName(instance)) return;

        AppLog.Info($"[mDNS] Instance lost -> {instance}");
        _reported.Remove(instance);
        _knownPeers.Remove(instance);
        PeerLost?.Invoke(instance);
    }

    void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();
        var updated = false;

        foreach (var record in records)
        {
            switch (record)
            {
                case SRVRecord srv:
                {
                    var key = NormalizeDnsName(srv.Name.ToString());
                    if (!IsEntangloServiceName(key)) break;

                    var target = NormalizeDnsName(srv.Target.ToString());
                    _srvCache[key] = (target, srv.Port);
                    AppLog.Info($"[mDNS] SRV {key} -> {target}:{srv.Port}");
                    _mdns?.SendQuery(target, DnsClass.IN, DnsType.A);
                    updated = true;
                    break;
                }
                case ARecord a:
                {
                    var key = NormalizeDnsName(a.Name.ToString());
                    _aCache[key] = a.Address.ToString();
                    AppLog.Info($"[mDNS] A   {key} -> {a.Address}");
                    updated = true;
                    break;
                }
                case PTRRecord ptr when IsEntangloServiceName(ptr.Name.ToString()):
                {
                    var instance = NormalizeDnsName(ptr.DomainName.ToString());
                    AppLog.Info($"[mDNS] PTR -> {instance}");
                    _mdns?.SendQuery(instance, DnsClass.IN, DnsType.SRV);
                    _mdns?.SendQuery(instance, DnsClass.IN, DnsType.TXT);
                    updated = true;
                    break;
                }
            }
        }

        if (updated) TryEmitPeers();
    }

    void TryEmitPeers()
    {
        foreach (var (instanceFqdn, (srvHost, port)) in _srvCache)
        {
            var instanceName = DecodeDnsLiteral(ShortInstanceName(instanceFqdn));
            if (instanceName.Equals(_deviceName, StringComparison.OrdinalIgnoreCase)) continue;

            string host;
            if (_aCache.TryGetValue(srvHost, out var ip))
                host = ip;
            else if (srvHost.EndsWith(".local"))
                host = srvHost;
            else
                host = srvHost;

            if (port == 0) continue;

            var peer = new DiscoveredPeer(instanceName, host, port);
            var existing = _knownPeers.TryGetValue(instanceFqdn, out var prev) ? prev : null;
            _knownPeers[instanceFqdn] = peer;
            if (existing != null
                && existing.Host == peer.Host
                && existing.Port == peer.Port
                && existing.InstanceName == peer.InstanceName)
                continue;
            if (_reported.Contains(instanceFqdn) && existing != null) continue;

            _reported.Add(instanceFqdn);
            AppLog.Info($"[mDNS] Peer found: {instanceName} @ {host}:{port}");
            PeerFound?.Invoke(peer);
        }
    }

    /// <summary>Decode DNS-escaped literals such as `\032` (space) emitted by
    /// Apple Bonjour when the instance name contains whitespace or punctuation.</summary>
    static string DecodeDnsLiteral(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 3 < raw.Length
                && char.IsDigit(raw[i + 1]) && char.IsDigit(raw[i + 2]) && char.IsDigit(raw[i + 3]))
            {
                int v = (raw[i + 1] - '0') * 100 + (raw[i + 2] - '0') * 10 + (raw[i + 3] - '0');
                if (v >= 0 && v <= 255) { sb.Append((char)v); i += 3; continue; }
            }
            if (raw[i] == '\\' && i + 1 < raw.Length)
            { sb.Append(raw[i + 1]); i++; continue; }
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    static bool IsEntangloServiceName(string name) =>
        NormalizeDnsName(name).Contains(ServiceTypeFqdnPart, StringComparison.OrdinalIgnoreCase)
        || NormalizeDnsName(name).EndsWith($".{ServiceType}", StringComparison.OrdinalIgnoreCase)
        || NormalizeDnsName(name).Equals(ServiceType, StringComparison.OrdinalIgnoreCase)
        || NormalizeDnsName(name).Equals(ServiceTypeLocal, StringComparison.OrdinalIgnoreCase);

    static string NormalizeDnsName(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    static string ShortInstanceName(string instanceFqdn)
    {
        var marker = $".{ServiceType}";
        var index = instanceFqdn.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? instanceFqdn[..index] : instanceFqdn.Split('.')[0];
    }

    public void Dispose()
    {
        if (_sd != null && _profile != null)
            try { _sd.Unadvertise(_profile); } catch { }
        if (_sd != null)
        {
            _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown -= OnServiceInstanceShutdown;
        }
        _sd?.Dispose();
        _mdns?.Stop();
    }
}
