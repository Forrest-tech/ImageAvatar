using ImageAvatar.Contracts.Services;

namespace ImageAvatar.Services;

public sealed class LogService : ILogService
{
    private readonly List<string> _entries = new(500);
    private readonly object _lock = new();

    public event EventHandler<string>? EntryAdded;

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 500) _entries.RemoveAt(0);
        }
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<string> GetEntries()
    {
        lock (_lock) return _entries.ToList();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
