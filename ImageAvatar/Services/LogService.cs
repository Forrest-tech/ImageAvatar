using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace ImageAvatar.Services;

public sealed class LogService : ILogService
{
    private readonly ObservableCollection<LogEntry> _entries = [];

    public ObservableCollection<LogEntry> Entries => _entries;

    public void Log(string message) => Log("Info", message);

    public void Log(string category, string message)
    {
        var entry = new LogEntry(DateTime.Now, category, message);
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(() => _entries.Add(entry));
        else
            _entries.Add(entry);
    }

    public void Clear()
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(() => _entries.Clear());
        else
            _entries.Clear();
    }
}
