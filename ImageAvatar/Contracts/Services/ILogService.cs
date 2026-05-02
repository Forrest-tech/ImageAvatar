using ImageAvatar.Models;
using System.Collections.ObjectModel;

namespace ImageAvatar.Contracts.Services;

public interface ILogService
{
    ObservableCollection<LogEntry> Entries { get; }
    void Log(string message);
    void Log(string category, string message);
    void Clear();
}
