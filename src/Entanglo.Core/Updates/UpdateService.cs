using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Entanglo.Core.Logging;
using Entanglo.Core.Net;

namespace Entanglo.Core.Updates;

public record ReleaseNote(
    [property: JsonPropertyName("version")]    string Version,
    [property: JsonPropertyName("date")]       string Date,
    [property: JsonPropertyName("title")]      string Title,
    [property: JsonPropertyName("highlights")] List<string> Highlights);

public sealed class LatestManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")]    public string Date    { get; set; } = "";
    [JsonPropertyName("title")]   public string Title   { get; set; } = "";
    [JsonPropertyName("url")]     public string Url     { get; set; } = "";
    [JsonPropertyName("sha256")]  public string Sha256  { get; set; } = "";
    [JsonPropertyName("size")]    public long   Size    { get; set; }
    [JsonPropertyName("highlights")] public List<string> Highlights { get; set; } = [];
}

public sealed record AvailableUpdate(
    string Version,
    string Title,
    string Url,
    List<string> Highlights,
    string? LocalInstallerPath = null,
    string? FromPeerName = null);

public sealed class UpdateService
{
    const string LatestUrl       = "https://entanglo.pages.dev/updates/latest-win.json";
    const string ReleaseNotesUrl = "https://entanglo.pages.dev/updates/release-notes.json";
    const string InstallerPrefix = "Entanglo-Setup-";
    const string InstallerSuffix = ".exe";

    readonly HttpClient _http;
    public string CurrentVersion { get; }

    public AvailableUpdate? AvailableUpdate { get; private set; }
    public List<ReleaseNote> ReleaseNotes { get; private set; } = [];
    public ReleaseNote? CurrentVersionNote =>
        ReleaseNotes.FirstOrDefault(n => n.Version == CurrentVersion);

    public event Action? Changed;

    /// <summary>
    /// Fired when an installer newer than the running build lands in the
    /// inbox via a peer push. The UI shows a countdown dialog and, on
    /// timeout, invokes RunInstallerAndQuit on the payload path.
    /// </summary>
    public event Action<string, string, string>? PeerPushedInstallerReady;
    // (version, installerPath, fromPeerName)

    ConnectionCoordinator? _coordinator;
    FileTransferService? _fileTransfers;

    /// <summary>Outbox where Paolo drops the freshly built installer.</summary>
    public static string OutboxDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Entanglo", "Updates");

    public UpdateService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Entanglo-Windows/1.0");
        CurrentVersion = ResolveCurrentVersion();
        Directory.CreateDirectory(OutboxDir);
        CleanupOldInstallers();
    }

    /// <summary>
    /// Wires peer-push: outgoing (we send our outbox installer to every
    /// connected peer) and incoming (we promote a received installer to
    /// AvailableUpdate and raise PeerPushedInstallerReady).
    /// </summary>
    public void WirePeerPush(ConnectionCoordinator coordinator, FileTransferService fileTransfers)
    {
        _coordinator = coordinator;
        _fileTransfers = fileTransfers;
        fileTransfers.IncomingFileComplete += OnIncomingFileComplete;
    }

    void OnIncomingFileComplete(FileTransfer t)
    {
        if (t.DestinationPath == null) return;
        EvaluateIncomingFile(t.DestinationPath, t.PeerName);
    }

    /// <summary>
    /// Push the currently-available installer to every connected peer.
    /// Returns the list of transfer ids so callers can wait for completion.
    /// </summary>
    public IReadOnlyList<string> PushUpdateToPeers()
    {
        if (AvailableUpdate?.LocalInstallerPath is not { } path) return Array.Empty<string>();
        if (_coordinator == null || _fileTransfers == null) return Array.Empty<string>();
        if (!File.Exists(path))
        {
            AppLog.Warning($"[Updates] Push: installer missing at {path}");
            return Array.Empty<string>();
        }
        // Only push .exe installers to Windows peers — Mac peers can't use them.
        var windowsPeers = _coordinator.ActivePeers
            .Where(p => p.Platform?.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (windowsPeers.Count == 0)
        {
            AppLog.Info("[Updates] Push: no Windows peers connected, skipping peer push");
            return Array.Empty<string>();
        }
        var ids = new List<string>();
        foreach (var peer in windowsPeers)
        {
            var id = _fileTransfers.SendFile(path, peer.DeviceId, peer.DeviceName, kind: "update");
            ids.Add(id);
        }
        AppLog.Info($"[Updates] Pushing {Path.GetFileName(path)} to {ids.Count} Windows peer(s)");
        return ids;
    }

    /// <summary>
    /// If <paramref name="path"/> is a valid Entanglo-Setup-X.Y.Z.exe file
    /// newer than the running build, promote it to AvailableUpdate and
    /// raise PeerPushedInstallerReady so the UI can show the countdown.
    /// </summary>
    public void EvaluateIncomingFile(string path, string? fromPeerName = null)
    {
        var version = ParseInstallerVersion(Path.GetFileName(path));
        if (version == null) return;
        if (SemverCompare(version, CurrentVersion) <= 0) return;

        var note = ReleaseNotes.FirstOrDefault(n => n.Version == version);
        AvailableUpdate = new AvailableUpdate(
            Version:     version,
            Title:       note?.Title ?? $"Entanglo {version}",
            Url:         path,
            Highlights:  note?.Highlights ?? new List<string>(),
            LocalInstallerPath: path,
            FromPeerName: fromPeerName);
        AppLog.Info($"[Updates] Peer-pushed installer promoted: {version} from {fromPeerName ?? "?"}");
        Changed?.Invoke();
        if (fromPeerName != null)
            PeerPushedInstallerReady?.Invoke(version, path, fromPeerName);
    }

    /// <summary>
    /// Deletes any Entanglo-Setup-*.exe in outbox/inbox whose version is
    /// strictly older than the running build. Prevents installer storage
    /// pile-up across releases.
    /// </summary>
    public void CleanupOldInstallers()
    {
        var dirs = new[] { OutboxDir, FileTransferService.DownloadsDir() };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, $"{InstallerPrefix}*{InstallerSuffix}"))
            {
                var v = ParseInstallerVersion(Path.GetFileName(file));
                if (v == null) continue;
                if (SemverCompare(v, CurrentVersion) < 0)
                {
                    try
                    {
                        File.Delete(file);
                        AppLog.Info($"[Updates] Removed obsolete installer {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning($"[Updates] Cannot delete {file}: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>Parses "Entanglo-Setup-0.1.48.exe" → "0.1.48".</summary>
    public static string? ParseInstallerVersion(string filename)
    {
        var m = Regex.Match(filename, @"^Entanglo-Setup-(\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Scans the outbox for the highest-versioned local installer and, if
    /// newer than current, exposes it as AvailableUpdate. Local trumps
    /// Netlify — if I've dropped a build in the outbox I want it used.
    /// </summary>
    public void ScanLocalOutbox()
    {
        if (!Directory.Exists(OutboxDir)) return;
        string? bestVersion = null;
        string? bestPath = null;
        foreach (var file in Directory.EnumerateFiles(OutboxDir, $"{InstallerPrefix}*{InstallerSuffix}"))
        {
            var v = ParseInstallerVersion(Path.GetFileName(file));
            if (v == null) continue;
            if (bestVersion == null || SemverCompare(v, bestVersion) > 0)
            {
                bestVersion = v;
                bestPath = file;
            }
        }
        if (bestVersion == null || bestPath == null) return;
        if (SemverCompare(bestVersion, CurrentVersion) <= 0) return;

        var note = ReleaseNotes.FirstOrDefault(n => n.Version == bestVersion);
        AvailableUpdate = new AvailableUpdate(
            Version:     bestVersion,
            Title:       note?.Title ?? $"Entanglo {bestVersion}",
            Url:         bestPath,
            Highlights:  note?.Highlights ?? new List<string>(),
            LocalInstallerPath: bestPath);
        AppLog.Info($"[Updates] Local outbox installer available: {bestVersion} at {bestPath}");
    }

    static string ResolveCurrentVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        if (v == null) return "0.0.0";
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(LoadReleaseNotesAsync(ct), CheckLatestAsync(ct));
        ScanLocalOutbox();
        Changed?.Invoke();
    }

    async Task LoadReleaseNotesAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(ReleaseNotesUrl, ct);
            var notes = JsonSerializer.Deserialize<List<ReleaseNote>>(json) ?? [];
            ReleaseNotes = notes.OrderByDescending(n => SemverKey(n.Version)).ToList();
            AppLog.Info($"[Updates] release-notes loaded: {ReleaseNotes.Count} entries");
        }
        catch (Exception ex)
        {
            AppLog.Warning($"[Updates] release-notes fetch failed: {ex.Message}");
        }
    }

    async Task CheckLatestAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(LatestUrl, ct);
            var manifest = JsonSerializer.Deserialize<LatestManifest>(json);
            if (manifest == null) return;
            if (SemverCompare(manifest.Version, CurrentVersion) > 0)
            {
                AvailableUpdate = new AvailableUpdate(
                    manifest.Version, manifest.Title, manifest.Url, manifest.Highlights);
                AppLog.Info($"[Updates] new build available: {manifest.Version}");
            }
            else
            {
                AvailableUpdate = null;
                AppLog.Info($"[Updates] up to date (manifest {manifest.Version} <= current {CurrentVersion})");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"[Updates] latest-win fetch failed: {ex.Message}");
        }
    }

    public async Task<string?> DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (AvailableUpdate == null) return null;
        // Local outbox / peer push: no HTTP needed.
        if (AvailableUpdate.LocalInstallerPath is { } local && File.Exists(local))
        {
            AppLog.Info($"[Updates] local installer already present at {local}");
            return local;
        }
        Directory.CreateDirectory(OutboxDir);
        var dest = Path.Combine(OutboxDir, $"{InstallerPrefix}{AvailableUpdate.Version}{InstallerSuffix}");

        AppLog.Info($"[Updates] downloading {AvailableUpdate.Url} -> {dest}");
        try
        {
            using var resp = await _http.GetAsync(
                AvailableUpdate.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buffer = new byte[64 * 1024];
            long readTotal = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                readTotal += n;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
            AppLog.Info($"[Updates] download complete ({readTotal} bytes)");
            return dest;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[Updates] download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download-if-needed, push to every connected peer, then run the
    /// installer helper and quit. Mirrors the Mac beginInstall flow.
    /// </summary>
    public async Task BeginInstallAsync(Action quitAction, bool pushToPeers = false, IProgress<double>? downloadProgress = null, CancellationToken ct = default)
    {
        var installerPath = AvailableUpdate?.LocalInstallerPath;
        if (installerPath == null || !File.Exists(installerPath))
            installerPath = await DownloadAsync(downloadProgress, ct);
        if (installerPath == null)
        {
            AppLog.Error("[Updates] BeginInstall: no installer available");
            return;
        }
        if (AvailableUpdate is { LocalInstallerPath: null } u)
            AvailableUpdate = u with { LocalInstallerPath = installerPath };

        if (pushToPeers)
        {
            var ids = PushUpdateToPeers();
            if (ids.Count > 0)
            {
                AppLog.Info($"[Updates] waiting for {ids.Count} peer push(es) before local install");
                await WaitForOutgoingTerminalsAsync(ids, ct);
            }
        }
        RunInstallerAndQuit(installerPath, quitAction);
    }

    Task WaitForOutgoingTerminalsAsync(IReadOnlyList<string> transferIds, CancellationToken ct)
    {
        if (_fileTransfers == null || transferIds.Count == 0) return Task.CompletedTask;
        var pending = new HashSet<string>(transferIds);
        var tcs = new TaskCompletionSource();
        void OnTerminal(FileTransfer t)
        {
            if (!pending.Contains(t.Id)) return;
            pending.Remove(t.Id);
            if (pending.Count == 0)
            {
                _fileTransfers.OutgoingFileTerminal -= OnTerminal;
                tcs.TrySetResult();
            }
        }
        _fileTransfers.OutgoingFileTerminal += OnTerminal;
        // Safety timeout — a stuck peer shouldn't hold the local install hostage.
        var timeout = Task.Delay(TimeSpan.FromMinutes(2), ct);
        return Task.WhenAny(tcs.Task, timeout).ContinueWith(_ =>
        {
            _fileTransfers.OutgoingFileTerminal -= OnTerminal;
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Spawns a detached cmd that waits for this process to exit, runs the
    /// installer silently, then relaunches the upgraded binary. The current
    /// process exits as soon as the helper is spawned.
    /// </summary>
    public void RunInstallerAndQuit(string installerPath, Action quitAction)
    {
        if (!File.Exists(installerPath))
        {
            AppLog.Error($"[Updates] installer missing at {installerPath}");
            return;
        }

        var pid = Environment.ProcessId;
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Path.Combine(AppContext.BaseDirectory, "Entanglo.Windows.exe");
        var helperPath = Path.Combine(Path.GetTempPath(), $"entanglo-update-{pid}.cmd");
        var logPath    = Path.Combine(Path.GetTempPath(), "entanglo-update.log");

        var script = $"""
@echo off
setlocal
set LOG="{logPath}"
echo [%date% %time%] === Entanglo auto-update PID={pid} === >> %LOG%
:wait
tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
echo [%date% %time%] running installer "{installerPath}" >> %LOG%
"{installerPath}" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS
echo [%date% %time%] installer exit code %ERRORLEVEL% >> %LOG%
echo [%date% %time%] relaunch "{exePath}" >> %LOG%
start "" "{exePath}"
del "{installerPath}" >nul 2>&1
del "%~f0" >nul 2>&1
""";
        try
        {
            File.WriteAllText(helperPath, script);
            var psi = new ProcessStartInfo("cmd.exe", $"/C start \"\" cmd /C \"{helperPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            AppLog.Info("[Updates] helper spawned, quitting…");
            quitAction();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[Updates] helper spawn failed: {ex.Message}");
        }
    }

    /// <summary>Numeric semver key for ordering "0.1.42" style versions.</summary>
    static long SemverKey(string v)
    {
        var parts = v.Split('.');
        long key = 0;
        for (int i = 0; i < 3; i++)
        {
            long n = (i < parts.Length && long.TryParse(parts[i], out var x)) ? x : 0;
            key = key * 10_000 + n;
        }
        return key;
    }

    static int SemverCompare(string a, string b) => SemverKey(a).CompareTo(SemverKey(b));
}
