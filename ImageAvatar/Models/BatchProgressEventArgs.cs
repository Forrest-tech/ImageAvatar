namespace ImageAvatar.Models;

public class BatchProgressEventArgs : EventArgs
{
    public int    Completed   { get; init; }
    public int    Total       { get; init; }
    public int    Failed      { get; init; }
    public string CurrentFile { get; init; } = string.Empty;

    public double Progress => Total > 0 ? (double)Completed / Total : 0;
}
