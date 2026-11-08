using System.Collections.ObjectModel;
using Entanglo.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Entanglo.Windows.Pages;

public sealed partial class LogsPage : Page
{
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = Entries;

        foreach (var entry in AppLog.Entries)
            Entries.Add(LogEntryViewModel.From(entry));
        UpdateCount();

        AppLog.EntryAdded += OnEntryAdded;
        Unloaded += (_, _) => AppLog.EntryAdded -= OnEntryAdded;
    }

    void OnEntryAdded(LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = LogEntryViewModel.From(entry);
            Entries.Add(vm);
            UpdateCount();
            LogList.ScrollIntoView(vm);
        });
    }

    void UpdateCount()
    {
        CountText.Text = Entries.Count == 1 ? "1 entry" : $"{Entries.Count} entries";
    }
}

public class LogEntryViewModel
{
    public string Time { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";

    public Brush LevelBrush { get; set; } = null!;
    public Brush LevelGlowBrush { get; set; } = null!;

    public static LogEntryViewModel From(LogEntry entry)
    {
        var (levelKey, glowKey) = entry.Level switch
        {
            LogLevel.Error   => ("EntangloDangerBrush", "EntangloDangerSoftBrush"),
            LogLevel.Warning => ("EntangloWarnBrush",   "EntangloWarnSoftBrush"),
            _                => ("EntangloAccentBrush", "EntangloAccentGlowBrush"),
        };
        return new LogEntryViewModel
        {
            Time    = entry.Time.ToString("HH:mm:ss"),
            Level   = entry.Level.ToString().ToUpperInvariant(),
            Message = entry.Message,
            LevelBrush     = (Brush)Application.Current.Resources[levelKey],
            LevelGlowBrush = (Brush)Application.Current.Resources[glowKey],
        };
    }
}
