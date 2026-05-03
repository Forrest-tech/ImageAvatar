using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.IO;

namespace ImageAvatar.Services;

public class StorageService : IStorageService, IDisposable
{
    private readonly List<PipelineFolder> _folders;
    private readonly List<FileSystemWatcher> _watchers = [];
    private string _rootPath = null!;

    public event EventHandler<FolderChangedEventArgs>? FolderChanged;

    public string RootPath
    {
        get => _rootPath;
        set
        {
            _rootPath = NormalizeWorkspaceRoot(value);
            RebuildPaths();
        }
    }

    public IReadOnlyList<PipelineFolder> Folders => _folders;

    /// <summary>
    /// If the user browsed *into* a pipeline folder (e.g. picked 00_提图队列 itself
    /// as the workspace), use its parent as the real workspace root. This keeps
    /// 30_抠图队列, 31_抠图完成, etc. as siblings of 00_提图队列 instead of nested
    /// inside it.
    /// </summary>
    private static string NormalizeWorkspaceRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                 Path.AltDirectorySeparatorChar));
        string[] pipelineNames =
        [
            "00_提图队列", "01_提图完成",
            "30_抠图队列", "31_抠图完成",
            "50_成品队列", "51_成品完成"
        ];
        if (pipelineNames.Any(n => string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase)))
            return Path.GetDirectoryName(path) ?? path;
        return path;
    }

    public StorageService(AppSettingsService settings)
    {
        _rootPath = !string.IsNullOrWhiteSpace(settings.WorkspaceRoot)
            ? NormalizeWorkspaceRoot(settings.WorkspaceRoot)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _folders =
        [
            new PipelineFolder { FolderName = "00_提图队列", LabelKey = "FolderExtractQueue",   Stage = PipelineStage.Extract,  Role = FolderRole.Queue },
            new PipelineFolder { FolderName = "01_提图完成", LabelKey = "FolderExtractDone",    Stage = PipelineStage.Extract,  Role = FolderRole.Done  },
            new PipelineFolder { FolderName = "30_抠图队列", LabelKey = "FolderRefineQueue",    Stage = PipelineStage.Refine,   Role = FolderRole.Queue },
            new PipelineFolder { FolderName = "31_抠图完成", LabelKey = "FolderRefineDone",     Stage = PipelineStage.Refine,   Role = FolderRole.Done  },
            new PipelineFolder { FolderName = "50_成品队列", LabelKey = "FolderFinalizeQueue",  Stage = PipelineStage.Finalize, Role = FolderRole.Queue },
            new PipelineFolder { FolderName = "51_成品完成", LabelKey = "FolderFinalizeDone",   Stage = PipelineStage.Finalize, Role = FolderRole.Done  },
        ];

        RebuildPaths();
    }

    private static readonly string[] ImageFilters =
        ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tiff", "*.tif", "*.webp"];

    private static int CountImages(string folder) =>
        ImageFilters.Sum(f => Directory.GetFiles(folder, f, SearchOption.TopDirectoryOnly).Length);

    private void RebuildPaths()
    {
        foreach (var folder in _folders)
        {
            folder.FullPath  = Path.Combine(_rootPath, folder.FolderName);
            folder.Exists    = Directory.Exists(folder.FullPath);
            folder.FileCount = folder.Exists ? CountImages(folder.FullPath) : 0;
        }
    }

    public void RefreshAll() => RebuildPaths();

    public void StartWatching()
    {
        StopWatching();

        foreach (var folder in _folders)
        {
            if (!folder.Exists)
                Directory.CreateDirectory(folder.FullPath);

            var watcher = new FileSystemWatcher(folder.FullPath, "*.png")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            var capturedFolder = folder;
            watcher.Created += (_, _) => OnFolderChanged(capturedFolder);
            watcher.Deleted += (_, _) => OnFolderChanged(capturedFolder);

            _watchers.Add(watcher);
        }
    }

    public void StopWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    private void OnFolderChanged(PipelineFolder folder)
    {
        int count = CountImages(folder.FullPath);
        folder.FileCount = count;
        FolderChanged?.Invoke(this, new FolderChangedEventArgs
        {
            FolderName   = folder.FolderName,
            FullPath     = folder.FullPath,
            NewFileCount = count
        });
    }

    public void Dispose() => StopWatching();
}
