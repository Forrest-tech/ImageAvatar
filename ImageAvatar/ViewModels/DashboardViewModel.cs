using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService _storage;

    [ObservableProperty]
    private string _rootPath;

    [ObservableProperty]
    private bool _isWatching;

    public ObservableCollection<FolderStatusItem> FolderItems { get; } = [];

    public DashboardViewModel(IStorageService storage)
    {
        _storage = storage;
        _rootPath = storage.RootPath;
        _storage.FolderChanged += OnFolderChanged;
        LoadFolders();
    }

    private void LoadFolders()
    {
        FolderItems.Clear();
        foreach (var f in _storage.Folders)
        {
            FolderItems.Add(new FolderStatusItem(f));
        }
    }

    private void OnFolderChanged(object? sender, FolderChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = FolderItems.FirstOrDefault(i => i.FolderName == e.FolderName);
            if (item is not null)
                item.FileCount = e.NewFileCount;
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        _storage.RefreshAll();
        LoadFolders();
    }

    [RelayCommand]
    private void ToggleWatching()
    {
        if (IsWatching)
        {
            _storage.StopWatching();
            IsWatching = false;
        }
        else
        {
            _storage.StartWatching();
            IsWatching = true;
        }
    }

    [RelayCommand]
    private void ApplyRootPath()
    {
        _storage.RootPath = RootPath;
        _storage.RefreshAll();
        LoadFolders();
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }
}

public partial class FolderStatusItem : ObservableObject
{
    public string FolderName { get; }
    public string LabelKey { get; }
    public PipelineStage Stage { get; }
    public FolderRole Role { get; }
    public string FullPath { get; }

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _exists;

    public string StageColor => Stage switch
    {
        PipelineStage.Extract  => "#4FC3F7",
        PipelineStage.Refine   => "#81C784",
        PipelineStage.Finalize => "#FFB74D",
        _ => "#FFFFFF"
    };

    public string RoleIcon => Role == FolderRole.Queue ? "" : "";

    public FolderStatusItem(PipelineFolder folder)
    {
        FolderName = folder.FolderName;
        LabelKey = folder.LabelKey;
        Stage = folder.Stage;
        Role = folder.Role;
        FullPath = folder.FullPath;
        FileCount = folder.FileCount;
        Exists = folder.Exists;
    }
}
