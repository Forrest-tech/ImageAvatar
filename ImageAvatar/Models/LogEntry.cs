namespace ImageAvatar.Models;

public record LogEntry(DateTime Timestamp, string Category, string Message)
{
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
    public string Display       => $"[{FormattedTime}] [{Category}] {Message}";
}
