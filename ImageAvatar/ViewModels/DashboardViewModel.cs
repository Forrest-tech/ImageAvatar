using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using ImageAvatar.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService             _storage;
    private readonly IPipelineCoordinatorService _coordinator;
    private readonly IImageExtractionService     _extraction;
    private readonly AppSettingsService          _settings;

    // ── Folder display ─────────────────────────────────────────────────────
    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private bool   _isWatching;

    // ── Extraction progress ────────────────────────────────────────────────
    [ObservableProperty] private bool   _isExtracting;
    [ObservableProperty] private double _extractionProgress;        // 0.0 – 1.0
    [ObservableProperty] private string _extractionStatus = string.Empty;
    [ObservableProperty] private string _currentFile      = string.Empty;
    [ObservableProperty] private bool   _isModelLoaded;

    public ObservableCollection<FolderStatusItem> FolderItems { get; } = [];

    public DashboardViewModel(
        IStorageService             storage,
        IPipelineCoordinatorService coordinator,
        IImageExtractionService     extraction,
        AppSettingsService          settings)
    {
        _storage     = storage;
        _coordinator = coordinator;
        _extraction  = extraction;
        _settings    = settings;

        _rootPath      = storage.RootPath;
        _isModelLoaded = extraction.IsModelLoaded;

        _storage.FolderChanged          += OnFolderChanged;
        _coordinator.ProgressChanged    += OnExtractionProgress;
        _coordinator.FileCompleted      += OnFileCompleted;

        LoadFolders();
    }

    // ── Folder display ─────────────────────────────────────────────────────

    private void LoadFolders()
    {
        FolderItems.Clear();
        foreach (var f in _storage.Folders)
            FolderItems.Add(new FolderStatusItem(f));
    }

    private void OnFolderChanged(object? sender, FolderChangedEventArgs e) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = FolderItems.FirstOrDefault(i => i.FolderName == e.FolderName);
            if (item is not null) item.FileCount = e.NewFileCount;
        });

    // ── Extraction progress callbacks ──────────────────────────────────────

    private void OnExtractionProgress(object? sender, ExtractionProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsExtracting       = true;
            IsModelLoaded      = _extraction.IsModelLoaded;
            ExtractionProgress = e.Progress;
            CurrentFile        = Path.GetFileName(e.FilePath);
            ExtractionStatus   = e.PendingCount > 0
                ? $"{CurrentFile}  (+{e.PendingCount} queued)"
                : CurrentFile;
        });
    }

    private void OnFileCompleted(object? sender, ExtractionResult e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsExtracting       = false;
            ExtractionProgress = 0;
            ExtractionStatus   = e.Success
                ? $"✓ {Path.GetFileName(e.SourcePath)}  ({e.Elapsed.TotalSeconds:F1}s)"
                : $"✗ {Path.GetFileName(e.SourcePath)}: {e.ErrorMessage}";
        });
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void Refresh()
    {
        _storage.RefreshAll();
        LoadFolders();
        IsModelLoaded = _extraction.IsModelLoaded;
    }

    [RelayCommand]
    private void ToggleWatching()
    {
        if (IsWatching)
        {
            _storage.StopWatching();
            _coordinator.Stop();
            IsWatching = false;
        }
        else
        {
            _storage.StartWatching();
            _coordinator.Start();
            IsWatching    = true;
            IsModelLoaded = _extraction.IsModelLoaded;
        }
    }

    [RelayCommand]
    private void ApplyRootPath()
    {
        _storage.RootPath = RootPath;
        _storage.RefreshAll();
        LoadFolders();
        _settings.WorkspaceRoot = RootPath;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }
}

// ── FolderStatusItem ───────────────────────────────────────────────────────

public partial class FolderStatusItem : ObservableObject
{
    public string       FolderName { get; }
    public string       LabelKey   { get; }
    public PipelineStage Stage     { get; }
    public FolderRole    Role      { get; }
    public string       FullPath   { get; }

    [ObservableProperty] private int  _fileCount;
    [ObservableProperty] private bool _exists;

    public string StageColor => Stage switch
    {
        PipelineStage.Extract  => "#4FC3F7",
        PipelineStage.Refine   => "#81C784",
        PipelineStage.Finalize => "#FFB74D",
        _                      => "#FFFFFF"
    };

    public string RoleIcon => Role == FolderRole.Queue ? "⬇" : "✔";

    public FolderStatusItem(PipelineFolder folder)
    {
        FolderName = folder.FolderName;
        LabelKey   = folder.LabelKey;
        Stage      = folder.Stage;
        Role       = folder.Role;
        FullPath   = folder.FullPath;
        FileCount  = folder.FileCount;
        Exists     = folder.Exists;
    }
}
