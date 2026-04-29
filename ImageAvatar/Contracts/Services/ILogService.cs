namespace ImageAvatar.Contracts.Services;

public interface ILogService
{
    event EventHandler<string> EntryAdded;
    void Log(string message);
    IReadOnlyList<string> GetEntries();
    void Clear();
}
