using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using ImageAvatar.Services;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class QcViewModel : ObservableObject
{
    private readonly IQcService           _qc;
    private readonly IStorageService      _storage;
    private readonly ILocalizationService _localization;

    // ── Collections & shared transform ────────────────────────────────────
    public ObservableCollection<QcItem> QcItems { get; } = [];

    /// Single transform instance shared by all ComparisonCanvas controls on this page.
    public SharedTransform Transform { get; } = new();

    // ── Bitmaps for the currently selected item ────────────────────────────
    [ObservableProperty] private SKBitmap? _originalBitmap;
    [ObservableProperty] private SKBitmap? _patternBitmap;
    [ObservableProperty] private SKBitmap? _mockupBitmap;

    // ── State ──────────────────────────────────────────────────────────────
    [ObservableProperty] private QcItem?        _selectedItem;
    [ObservableProperty] private ComparisonMode _mode = ComparisonMode.Grid;
    [ObservableProperty] private double         _overlayOpacity = 0.5;
    [ObservableProperty] private bool           _isLoading;

    public bool IsGridMode    => Mode == ComparisonMode.Grid;
    public bool IsOverlayMode => Mode == ComparisonMode.Overlay;
    public bool HasSelectedItem => SelectedItem is not null;

    public string OverlayOpacityPercent =>
        $"{OverlayOpacity:P0}";

    public string ItemCountSummary =>
        $"{QcItems.Count(i => i.IsApproved)} ✓  " +
        $"{QcItems.Count(i => i.IsRejected)} ✗  " +
        $"{QcItems.Count(i => i.IsPending)} …";

    public QcViewModel(
        IQcService           qc,
        IStorageService      storage,
        ILocalizationService localization)
    {
        _qc           = qc;
        _storage      = storage;
        _localization = localization;

        _localization.LanguageChanged += (_, _) =>
            OnPropertyChanged(nameof(OverlayOpacityPercent));
    }

    // ── Property-changed side-effects ──────────────────────────────────────

    partial void OnSelectedItemChanged(QcItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedItem));
        Transform.Reset();
        FreeBitmaps();

        if (value is not null)
            _ = LoadBitmapsAsync(value);
    }

    partial void OnModeChanged(ComparisonMode value)
    {
        OnPropertyChanged(nameof(IsGridMode));
        OnPropertyChanged(nameof(IsOverlayMode));
    }

    partial void OnOverlayOpacityChanged(double value) =>
        OnPropertyChanged(nameof(OverlayOpacityPercent));

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void LoadItems()
    {
        var mockupFolder  = GetFolder("51_成品完成");
        var patternFolder = GetFolder("01_提图完成");
        var sourceFolder  = GetFolder("00_提图队列");

        FreeBitmaps();
        SelectedItem = null;
        QcItems.Clear();

        foreach (var item in _qc.LoadItems(mockupFolder, patternFolder, sourceFolder))
            QcItems.Add(item);

        OnPropertyChanged(nameof(ItemCountSummary));
    }

    [RelayCommand]
    private async Task Approve(QcItem? item)
    {
        if (item is null || !item.IsPending) return;
        await _qc.ApproveAsync(item);
        OnPropertyChanged(nameof(ItemCountSummary));
    }

    [RelayCommand]
    private async Task Reject(QcItem? item)
    {
        if (item is null || !item.IsPending) return;
        await _qc.RejectAsync(item);
        OnPropertyChanged(nameof(ItemCountSummary));
    }

    [RelayCommand]
    private void SetGridMode() => Mode = ComparisonMode.Grid;

    [RelayCommand]
    private void SetOverlayMode() => Mode = ComparisonMode.Overlay;

    [RelayCommand]
    private void ResetZoom() => Transform.Reset();

    // ── Bitmap loading ─────────────────────────────────────────────────────

    private async Task LoadBitmapsAsync(QcItem item)
    {
        IsLoading = true;
        try
        {
            var (orig, pat, mock) = await Task.Run(() =>
            (
                LoadSafe(item.OriginalPath),
                LoadSafe(item.PatternPath),
                LoadSafe(item.MockupPath)
            ));

            // Marshal to UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                OriginalBitmap = orig;
                PatternBitmap  = pat;
                MockupBitmap   = mock;
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static SKBitmap? LoadSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return SKBitmap.Decode(path); }
        catch { return null; }
    }

    private void FreeBitmaps()
    {
        OriginalBitmap?.Dispose(); OriginalBitmap = null;
        PatternBitmap?.Dispose();  PatternBitmap  = null;
        MockupBitmap?.Dispose();   MockupBitmap   = null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string GetFolder(string name) =>
        _storage.Folders.First(f => f.FolderName == name).FullPath;
}
