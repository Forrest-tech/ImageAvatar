using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using ImageAvatar.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class BatchProcessorViewModel : ObservableObject
{
    private readonly IBatchMockupService  _batch;
    private readonly IStorageService      _storage;
    private readonly ILocalizationService _localization;
    private readonly AppSettingsService   _settings;

    // ── State ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isBatchRunning;
    [ObservableProperty] private double _batchProgress;       // 0.0 – 1.0
    [ObservableProperty] private string _batchStatus = string.Empty;
    [ObservableProperty] private int    _completedCount;
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private int    _failedCount;
    [ObservableProperty] private string _templatesFolder;

    public string ProgressSummary =>
        $"{CompletedCount} / {TotalCount}  ({FailedCount} {GetResource("LabelBatchFailed")})";

    public ObservableCollection<BatchItemStatus> BatchItems { get; } = [];

    private CancellationTokenSource _cts = new();

    // Tracks current state for re-localization on language change
    private enum BatchState { Idle, Processing, Completed, Cancelled }
    private BatchState _currentState = BatchState.Idle;

    public BatchProcessorViewModel(
        IBatchMockupService  batch,
        IStorageService      storage,
        ILocalizationService localization,
        AppSettingsService   settings)
    {
        _batch        = batch;
        _storage      = storage;
        _localization = localization;
        _settings     = settings;

        _templatesFolder = settings.TemplatesFolder;

        _batch.FileCompleted          += OnFileCompleted;
        _localization.LanguageChanged += (_, _) => RefreshStatus();

        RefreshStatus();
    }

    // ── CanExecute notifications ───────────────────────────────────────────

    partial void OnIsBatchRunningChanged(bool value)
    {
        StartBatchCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProgressSummary));
    }

    partial void OnCompletedCountChanged(int value) =>
        OnPropertyChanged(nameof(ProgressSummary));

    partial void OnTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(ProgressSummary));

    partial void OnFailedCountChanged(int value) =>
        OnPropertyChanged(nameof(ProgressSummary));

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartBatch))]
    private async Task StartBatch()
    {
        var inputFolder  = GetFolder("50_成品队列");
        var outputFolder = GetFolder("51_成品完成");

        if (!Directory.Exists(inputFolder) ||
            Directory.GetFiles(inputFolder, "*.png").Length == 0)
        {
            BatchStatus = GetResource("LabelBatchNoPatterns");
            return;
        }

        if (!Directory.Exists(TemplatesFolder) ||
            Directory.GetFiles(TemplatesFolder, "*.png").Length == 0)
        {
            BatchStatus = GetResource("LabelNoTemplates");
            return;
        }

        BatchItems.Clear();
        CompletedCount = 0;
        TotalCount     = 0;
        FailedCount    = 0;
        BatchProgress  = 0;
        IsBatchRunning = true;
        _currentState  = BatchState.Processing;
        RefreshStatus();

        _cts = new CancellationTokenSource();

        var progress = new Progress<BatchProgressEventArgs>(OnProgress);

        try
        {
            await _batch.RunAsync(inputFolder, outputFolder, TemplatesFolder, progress, _cts.Token);
            _storage.RefreshAll();
            _currentState = BatchState.Completed;
        }
        catch (OperationCanceledException)
        {
            _currentState = BatchState.Cancelled;
        }
        finally
        {
            IsBatchRunning = false;
            RefreshStatus();
        }
    }

    private bool CanStartBatch() => !IsBatchRunning;

    [RelayCommand(CanExecute = nameof(CanCancelBatch))]
    private void CancelBatch() => _cts.Cancel();

    private bool CanCancelBatch() => IsBatchRunning;

    [RelayCommand]
    private void BrowseTemplatesFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = GetResource("LabelTemplatesFolder"),
            InitialDirectory = Directory.Exists(TemplatesFolder) ? TemplatesFolder : string.Empty
        };
        if (dialog.ShowDialog() == true)
        {
            TemplatesFolder          = dialog.FolderName;
            _settings.TemplatesFolder = dialog.FolderName;
            _settings.Save();
        }
    }

    // ── Event callbacks ────────────────────────────────────────────────────

    private void OnProgress(BatchProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TotalCount     = e.Total;
            CompletedCount = e.Completed;
            FailedCount    = e.Failed;
            BatchProgress  = e.Progress;
        });
    }

    private void OnFileCompleted(object? sender, MockupResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            BatchItems.Insert(0, new BatchItemStatus
            {
                PatternName  = Path.GetFileNameWithoutExtension(result.PatternPath),
                TemplateName = Path.GetFileNameWithoutExtension(result.TemplatePath),
                OutputPath   = result.OutputPath,
                Success      = result.Success,
                ErrorMessage = result.ErrorMessage,
                Elapsed      = result.Elapsed
            });
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        string key = _currentState switch
        {
            BatchState.Processing => "LabelBatchProcessing",
            BatchState.Completed  => "LabelBatchCompleted",
            BatchState.Cancelled  => "LabelBatchCancelled",
            _                     => "LabelBatchIdle"
        };
        BatchStatus = GetResource(key);
    }

    private string GetFolder(string folderName) =>
        _storage.Folders.First(f => f.FolderName == folderName).FullPath;

    private static string GetResource(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}
