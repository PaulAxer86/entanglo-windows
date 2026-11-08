using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Entanglo.Core.Net;

public record TrustedDevice(string DeviceId, string DeviceName, DateTimeOffset PairedAt);

/// <summary>DPAPI-encrypted JSON trust store. PROTOCOL.md §9.</summary>
public sealed class TrustStore
{
    static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Entanglo", "trust.json.enc");

    readonly List<TrustedDevice> _devices = [];

    public IReadOnlyList<TrustedDevice> Devices => _devices.AsReadOnly();

    public void Load()
    {
        if (!File.Exists(StorePath)) return;
        try
        {
            var encrypted = File.ReadAllBytes(StorePath);
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize<List<TrustedDevice>>(json);
            if (loaded != null) { _devices.Clear(); _devices.AddRange(loaded); }
        }
        catch { /* corrupt store — start fresh */ }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_devices));
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, encrypted);
    }

    public bool IsTrusted(string deviceId) =>
        _devices.Any(d => d.DeviceId == deviceId);

    public void Trust(string deviceId, string deviceName)
    {
        if (!IsTrusted(deviceId))
            _devices.Add(new TrustedDevice(deviceId, deviceName, DateTimeOffset.UtcNow));
        Save();
    }

    public void Revoke(string deviceId)
    {
        _devices.RemoveAll(d => d.DeviceId == deviceId);
        Save();
    }
}
