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

public class PipelineFolder
{
    public string FolderName { get; init; } = string.Empty;
    public string LabelKey { get; init; } = string.Empty;
    public PipelineStage Stage { get; init; }
    public FolderRole Role { get; init; }
    public string FullPath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public bool Exists { get; set; }
}
