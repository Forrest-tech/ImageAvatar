using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageAvatar.Models;

public enum PipelineStage
{
    Extract,
    Refine,
    Finalize
}

public enum FolderRole
{
    Queue,
    Done
}

public partial class PipelineFolder : ObservableObject
{
    public string        FolderName { get; init; } = string.Empty;
    public string        LabelKey   { get; init; } = string.Empty;
    public PipelineStage Stage      { get; init; }
    public FolderRole    Role       { get; init; }
    public string        FullPath   { get; set; }  = string.Empty;

    [ObservableProperty] private int  _fileCount;
    [ObservableProperty] private bool _exists;
}
