namespace ImageAvatar.Models;

public class FolderChangedEventArgs : EventArgs
{
    public string FolderName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public int NewFileCount { get; init; }
}
