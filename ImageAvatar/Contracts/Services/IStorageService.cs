using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

public interface IStorageService
{
    string RootPath { get; set; }
    IReadOnlyList<PipelineFolder> Folders { get; }
    void StartWatching();
    void StopWatching();
    void RefreshAll();
    event EventHandler<FolderChangedEventArgs> FolderChanged;
}
