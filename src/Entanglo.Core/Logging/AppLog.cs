namespace Entanglo.Core.Logging;

public enum LogLevel { Info, Warning, Error }
public record LogEntry(DateTimeOffset Time, LogLevel Level, string Message);

public static class AppLog
{
    static readonly List<LogEntry> _entries = [];
    static readonly object _lock = new();
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Entanglo",
        "entanglo.log");

    public static event Action<LogEntry>? EntryAdded;

    public static IReadOnlyList<LogEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    public static void Info(string msg)    => Add(LogLevel.Info,    msg);
    public static void Warning(string msg) => Add(LogLevel.Warning, msg);
    public static void Error(string msg)   => Add(LogLevel.Error,   msg);

    static void Add(LogLevel level, string msg)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, msg);
        lock (_lock)
        {
            _entries.Add(entry);
            TryAppendFile(entry);
        }
        EntryAdded?.Invoke(entry);
        // Mirror to debug output
        System.Diagnostics.Debug.WriteLine($"[{level}] {msg}");
    }

    static void TryAppendFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{entry.Time:O} [{entry.Level}] {entry.Message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the app path.
        }
    }
}
