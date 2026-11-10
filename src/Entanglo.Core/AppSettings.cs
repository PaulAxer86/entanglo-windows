using System.Text.Json;

namespace Entanglo.Core;

public sealed class AppSettings
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Entanglo", "settings.json");

    public bool   ReceiveRemoteInput { get; set; } = true;
    public bool   AutoDialTrusted    { get; set; } = true;
    public EdgeSwitchConfig EdgeConfig { get; set; } = new();
    // Per-session pointer speed multiplier applied to relative mouse motion (not scroll).
    // Clamped to [0.25, 4.0] at use; NaN or values out of range fall back to 1.0.
    public double PointerSpeed { get; set; } = 1.0;

    public event Action? Changed;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }
}

public sealed class EdgeSwitchConfig
{
    public bool   Enabled     { get; set; } = true;
    public string? LeftPeer   { get; set; }
    public string? RightPeer  { get; set; }
    public string? TopPeer    { get; set; }
    public string? BottomPeer { get; set; }
}
