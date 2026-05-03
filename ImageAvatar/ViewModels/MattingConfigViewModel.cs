using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using System.IO;
using System.Net.Http;

namespace ImageAvatar.ViewModels;

public partial class MattingConfigViewModel : ObservableObject
{
    // rembg's release-hosted U-2-Net weights (~176 MB). Stable URL since 2020.
    private const string U2NetDownloadUrl =
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx";

    private readonly IMattingService          _matting;
    private readonly IImageExtractionService  _extraction;
    private readonly AppSettingsService       _settings;
    private readonly ILogService              _log;

    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private string _modelPath;
    [ObservableProperty] private string _inputFolder;
    [ObservableProperty] private bool   _isModelLoaded;
    [ObservableProperty] private string _modelStatus;
    [ObservableProperty] private bool   _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartDownload))]
    private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;       // 0..100
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private bool   _u2NetExists;
    [ObservableProperty] private string _u2NetStatus = string.Empty;

    public bool CanStartDownload => !IsDownloading;

    public MattingConfigViewModel(
        IMattingService          matting,
        IImageExtractionService  extraction,
        AppSettingsService       settings,
        ILogService              log)
    {
        _matting      = matting;
        _extraction   = extraction;
        _settings     = settings;
        _log          = log;
        _modelPath    = settings.MattingModelPath;
        _inputFolder  = settings.MattingInputFolder;
        _isModelLoaded = matting.IsModelLoaded;
        _modelStatus  = matting.ModelStatus;
        RefreshU2NetStatus();
    }

    private void RefreshU2NetStatus()
    {
        var path = _settings.ModelPath;
        if (File.Exists(path))
        {
            var sizeMb = new FileInfo(path).Length / (1024d * 1024d);
            U2NetExists = true;
            U2NetStatus = _extraction.IsModelLoaded
                ? $"✓ 已加载  ({sizeMb:F1} MB)"
                : $"✓ 文件已就位 ({sizeMb:F1} MB)，但未加载";
        }
        else
        {
            U2NetExists = false;
            U2NetStatus = "✗ 未下载（抠图功能依赖此模型作为备用引擎）";
        }
    }

    // ── Browse / load model ────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "选择 PaddleSeg 抠图 ONNX 模型文件",
            Filter = "ONNX 模型 (*.onnx)|*.onnx|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            ModelPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task LoadModelAsync()
    {
        if (string.IsNullOrWhiteSpace(ModelPath)) return;
        IsLoading   = true;
        ModelStatus = "加载中…";
        try
        {
            await Task.Run(() => _matting.LoadModelAsync(ModelPath));
            _settings.MattingModelPath = ModelPath;
            _settings.Save();
            IsModelLoaded = true;
            ModelStatus   = _matting.ModelStatus;
        }
        catch (Exception ex)
        {
            IsModelLoaded = false;
            ModelStatus   = $"✗ {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    // ── Browse input folder ────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseInputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "选择要抠图的图片文件夹",
            InitialDirectory = Directory.Exists(InputFolder) ? InputFolder : string.Empty
        };
        if (dialog.ShowDialog() == true)
            InputFolder = dialog.FolderName;
    }

    // ── Save ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        _settings.MattingInputFolder = InputFolder;
        _settings.MattingModelPath   = ModelPath;
        _settings.Save();
        ModelStatus = "✓ 配置已保存";
    }

    // ── Download U-2-Net (~176 MB) ─────────────────────────────────────────

    [RelayCommand]
    private async Task DownloadU2NetAsync()
    {
        if (IsDownloading) return;

        var destPath = _settings.ModelPath;
        var destDir  = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(destDir);

        // Stream to a temp file so a partial/failed download can't masquerade as
        // a real model on next launch.
        var tempPath = destPath + ".part";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        IsDownloading    = true;
        DownloadProgress = 0;
        DownloadStatus   = "正在连接…";
        _downloadCts     = new CancellationTokenSource();
        var ct           = _downloadCts.Token;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageAvatar/1.0");

            using var response = await http.GetAsync(
                U2NetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            DownloadStatus = totalBytes > 0
                ? $"下载中  0 / {FormatMb(totalBytes)}"
                : "下载中…";

            using var src = await response.Content.ReadAsStreamAsync(ct);
            using (var dst = File.Create(tempPath))
            {
                var buffer  = new byte[81920];
                long copied = 0;
                int  read;
                int  reportEvery = 0;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;

                    // Update UI ~10 times/sec (every ~64 chunks of 80 KB ≈ 5 MB)
                    if (++reportEvery >= 64 || (totalBytes > 0 && copied == totalBytes))
                    {
                        reportEvery = 0;
                        if (totalBytes > 0)
                        {
                            DownloadProgress = copied * 100.0 / totalBytes;
                            DownloadStatus   = $"下载中  {FormatMb(copied)} / {FormatMb(totalBytes)}";
                        }
                        else
                        {
                            DownloadStatus = $"下载中  {FormatMb(copied)}";
                        }
                    }
                }
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tempPath, destPath);

            DownloadProgress = 100;
            DownloadStatus   = "✓ 下载完成，正在加载…";
            _log.Log("抠图", $"U-2-Net 已下载至 {destPath}");

            // Auto-load so matting can run immediately
            await _extraction.LoadModelAsync(destPath);
            DownloadStatus = "✓ 下载并加载完成，可以开始抠图";
            _log.Log("抠图", "U-2-Net 模型已加载");
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "已取消";
            TryDeleteTemp(tempPath);
        }
        catch (Exception ex)
        {
            DownloadStatus = $"✗ 下载失败: {ex.Message}";
            _log.Log("抠图", $"U-2-Net 下载失败: {ex.Message}");
            TryDeleteTemp(tempPath);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            RefreshU2NetStatus();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private static string FormatMb(long bytes) => $"{bytes / (1024d * 1024d):F1} MB";

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
