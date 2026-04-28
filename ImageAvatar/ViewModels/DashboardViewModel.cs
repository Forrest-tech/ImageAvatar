using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using System.IO;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService             _storage;
    private readonly IPipelineCoordinatorService _coordinator;
    private readonly IImageExtractionService     _extraction;
    private readonly ILocalizationService        _localization;
    private readonly AppSettingsService          _settings;

    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private bool   _isWatching;
    [ObservableProperty] private bool   _isExtracting;
    [ObservableProperty] private double _extractionProgress;
    [ObservableProperty] private string _extractionStatus = string.Empty;
    [ObservableProperty] private string _currentFile      = string.Empty;
    [ObservableProperty] private bool   _isModelLoaded;

    public string ServiceStatusText => IsWatching ? GetResource("LabelServiceRunning") : GetResource("LabelServiceStopped");
    public string ServiceToggleText => IsWatching ? GetResource("BtnStopService")      : GetResource("BtnStartService");

    public DashboardViewModel(
        IStorageService             storage,
        IPipelineCoordinatorService coordinator,
        IImageExtractionService     extraction,
        ILocalizationService        localization,
        AppSettingsService          settings)
    {
        _storage      = storage;
        _coordinator  = coordinator;
        _extraction   = extraction;
        _localization = localization;
        _settings     = settings;

        _rootPath      = storage.RootPath;
        _isModelLoaded = extraction.IsModelLoaded;

        _storage.FolderChanged       += OnFolderChanged;
        _coordinator.ProgressChanged += OnExtractionProgress;
        _coordinator.FileCompleted   += OnFileCompleted;

        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ServiceStatusText));
            OnPropertyChanged(nameof(ServiceToggleText));
        };
    }

    partial void OnIsWatchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ServiceStatusText));
        OnPropertyChanged(nameof(ServiceToggleText));
    }

    private void OnFolderChanged(object? sender, FolderChangedEventArgs e) { }

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

    [RelayCommand]
    private void Refresh()
    {
        _storage.RefreshAll();
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
        _settings.WorkspaceRoot = RootPath;
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseSourceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = GetResource("LabelRootPath"),
            InitialDirectory = Directory.Exists(RootPath) ? RootPath : string.Empty
        };
        if (dialog.ShowDialog() == true)
            RootPath = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private static string GetResource(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}
