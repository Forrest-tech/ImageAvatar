namespace ImageAvatar.Models;

public class ExtractionProgressEventArgs : EventArgs
{
    public string FilePath { get; init; } = string.Empty;
    public double Progress { get; init; }
    public int PendingCount { get; init; }
}
