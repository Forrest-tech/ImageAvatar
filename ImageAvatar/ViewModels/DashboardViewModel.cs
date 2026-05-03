using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IStorageService     _storage;
    private readonly IMattingService     _matting;
    private readonly IBatchMockupService _synthesis;
    private readonly ICropService                _crop;
    private readonly IPromptService              _prompt;
    private readonly IGenService                 _gen;
    private readonly ILogService                 _log;
    private readonly AppSettingsService          _settings;

    // ── Workspace ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _rootPath;

    // ── Service running states ─────────────────────────────────────────────
    [ObservableProperty] private bool   _isCropRunning;
    [ObservableProperty] private string _cropStatusText = "● 未启动";
    [ObservableProperty] private bool _isPromptRunning;
    [ObservableProperty] private bool _isGenRunning;
    [ObservableProperty] private bool   _isMattingRunning;
    [ObservableProperty] private string _mattingStatusText = "● 未启动";
    [ObservableProperty] private bool   _isSynthesisRunning;
    [ObservableProperty] private string _synthesisStatusText = "● 未启动";

    private CancellationTokenSource? _synthesisCts;

    public DashboardViewModel(
        IStorageService     storage,
        IMattingService     matting,
        IBatchMockupService synthesis,
        ICropService                crop,
        IPromptService              prompt,
        IGenService                 gen,
        ILogService                 log,
        AppSettingsService          settings)
    {
        _storage   = storage;
        _matting   = matting;
        _synthesis = synthesis;
        _crop      = crop;
        _prompt    = prompt;
        _gen       = gen;
        _log       = log;
        _settings  = settings;
        _rootPath  = storage.RootPath;
    }

    // ── Workspace commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseWorkspace()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "选择工作区根目录（如 D:/PodFlow）",
            InitialDirectory = Directory.Exists(RootPath) ? RootPath : string.Empty
        };
        if (dialog.ShowDialog() != true)
        {
            _log.Log("工作区", "已取消选择");
            return;
        }

        var picked = dialog.FolderName;
        _log.Log("工作区", $"用户选中: {picked}");

        // Assign through StorageService first so normalization runs (e.g. user
        // picked 00_提图队列 directly → use parent), then read the canonical
        // value back for display & persistence.
        _storage.RootPath       = picked;
        var canonical           = _storage.RootPath;
        var oldRootPath         = RootPath;
        _log.Log("工作区", $"规范化路径: {canonical}（旧值: {oldRootPath}）");

        // Force the WPF binding to refresh. Setting to a known-different value
        // first guarantees the [ObservableProperty] setter fires PropertyChanged
        // even if the canonical value happens to equal the old one (re-picking
        // the same folder, or normalisation collapsing back to the same parent).
        // Wrap in Dispatcher.Invoke so the bound TextBlock sees both changes
        // even if this command happened to be invoked off the UI thread.
        var ui = Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess())
            ui.Invoke(() => { RootPath = string.Empty; RootPath = canonical; });
        else
        {
            RootPath = string.Empty;
            RootPath = canonical;
        }

        _settings.WorkspaceRoot = canonical;

        // If the user ever pinned an absolute MattingInputFolder, it would
        // override the new workspace and silently keep matting on the old path.
        // Clearing it makes the workspace change actually take effect.
        if (!string.IsNullOrWhiteSpace(_settings.MattingInputFolder))
        {
            _log.Log("工作区", $"清除旧的抠图输入路径覆盖: {_settings.MattingInputFolder}");
            _settings.MattingInputFolder = string.Empty;
        }

        _settings.Save();
        _storage.RefreshAll();

        if (!string.Equals(picked, canonical, StringComparison.OrdinalIgnoreCase))
            _log.Log("工作区", $"已识别为流水线子目录，工作区切换至父目录 {canonical}");
        else
            _log.Log("工作区", $"工作区已切换至 {canonical}");
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (Directory.Exists(RootPath))
            System.Diagnostics.Process.Start("explorer.exe", RootPath);
    }

    // ── 自动裁图 ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleCrop()
    {
        if (IsCropRunning)
        {
            _crop.Stop();
            IsCropRunning  = false;
            CropStatusText = "● 未启动";
            _log.Log("裁图", "服务已停止");
            return;
        }

        var sw = Stopwatch.StartNew();
        IsCropRunning  = true;
        CropStatusText = "● 运行中";
        _log.Log("裁图", "服务已启动");

        _ = Task.Run(async () =>
        {
            bool cancelled = false;
            try   { await _crop.StartAsync(); }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex)
            {
                cancelled = true;
                _log.Log("裁图", $"异常: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                var elapsed = FormatElapsed(sw.Elapsed);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsCropRunning  = false;
                    CropStatusText = cancelled
                        ? "● 未启动"
                        : $"● 裁剪完成（耗时 {elapsed}）";
                });
                if (!cancelled) _log.Log("裁图", $"完成，耗时 {elapsed}");
            }
        });
    }

    // ── 提示词生成 ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task TogglePromptAsync()
    {
        if (IsPromptRunning)
        {
            _prompt.Stop();
            IsPromptRunning = false;
            _log.Log("提示词", "服务已停止");
        }
        else
        {
            IsPromptRunning = true;
            await Task.Run(() => _prompt.StartAsync());
            _log.Log("提示词", "服务已启动");
        }
    }

    // ── 生图 ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleGenAsync()
    {
        if (IsGenRunning)
        {
            _gen.Stop();
            IsGenRunning = false;
            _log.Log("生图", "服务已停止");
        }
        else
        {
            IsGenRunning = true;
            await Task.Run(() => _gen.StartAsync());
            _log.Log("生图", "服务已启动");
        }
    }

    // ── 自动抠图 (PaddleSeg matting) ──────────────────────────────────────

    [RelayCommand]
    private void ToggleMatting()
    {
        if (IsMattingRunning)
        {
            _matting.Stop();
            IsMattingRunning  = false;
            MattingStatusText = "● 未启动";
            _log.Log("抠图", "服务已停止");
            return;
        }

        // Resolve input/output from the (already-normalized) workspace root. The
        // matting stage always writes to {root}/31_抠图完成 so results stay inside
        // the workspace and the dashboard tile updates.
        var root         = RootPath;
        var queueDir     = Path.Combine(root, "30_抠图队列");
        var extractedDir = Path.Combine(root, "01_提图完成");
        var outputDir    = Path.Combine(root, "31_抠图完成");

        // Input priority: user-configured > 30_抠图队列 (if has files) > 01_提图完成
        // (auto-flow from previous stage) > 30_抠图队列 (empty default).
        string inputDir;
        var configured = _settings.MattingInputFolder;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            inputDir = configured;
        }
        else if (HasImages(queueDir))
        {
            inputDir = queueDir;
        }
        else if (HasImages(extractedDir))
        {
            inputDir = extractedDir;
            _log.Log("抠图", "30_抠图队列 为空，自动从 01_提图完成 读取");
        }
        else
        {
            inputDir = queueDir;
        }

        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sw = Stopwatch.StartNew();
        IsMattingRunning  = true;
        MattingStatusText = "● 运行中";
        _log.Log("抠图", "服务已启动");

        _ = Task.Run(async () =>
        {
            bool cancelled = false;
            Models.MattingRunResult? result = null;
            try
            {
                result = await _matting.StartAsync(inputDir, outputDir);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex)
            {
                cancelled = true;
                _log.Log("抠图", $"异常: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                var elapsed = FormatElapsed(sw.Elapsed);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsMattingRunning  = false;
                    MattingStatusText = cancelled
                        ? "● 未启动"
                        : (result?.ToShortStatus() ?? "● 未产出") + $"（耗时 {elapsed}）";
                });
                _storage.RefreshAll();
                if (!cancelled) _log.Log("抠图", $"完成，耗时 {elapsed}");
            }
        });
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)  return $"{ts.TotalSeconds:F1} 秒";
        if (ts.TotalMinutes < 60)  return $"{(int)ts.TotalMinutes} 分 {ts.Seconds} 秒";
        return $"{(int)ts.TotalHours} 时 {ts.Minutes} 分 {ts.Seconds} 秒";
    }

    private static readonly string[] ImageGlobs =
        ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tiff", "*.tif", "*.webp"];

    private static int CountImagesIn(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        return ImageGlobs.Sum(p => Directory.GetFiles(folder, p, SearchOption.TopDirectoryOnly).Length);
    }

    private static bool HasImages(string folder) => CountImagesIn(folder) > 0;

    // ── 自动合成 (batch mockup) ────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleSynthesisAsync()
    {
        if (IsSynthesisRunning)
        {
            _synthesisCts?.Cancel();
            _synthesisCts    = null;
            IsSynthesisRunning = false;
            SynthesisStatusText = "● 未启动";
            _log.Log("合成", "服务已停止");
            return;
        }

        var sw = Stopwatch.StartNew();
        _synthesisCts        = new CancellationTokenSource();
        IsSynthesisRunning   = true;
        SynthesisStatusText  = "● 运行中";
        _log.Log("合成", "服务已启动");

        var inputFolder     = Path.Combine(RootPath, "50_成品队列");
        var outputFolder    = Path.Combine(RootPath, "51_成品完成");
        var templatesFolder = _settings.TemplatesFolder;
        var cts             = _synthesisCts;

        _ = Task.Run(async () =>
        {
            bool cancelled = false;
            try
            {
                await _synthesis.RunAsync(inputFolder, outputFolder, templatesFolder, ct: cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { cancelled = true; _log.Log("合成", $"异常: {ex.Message}"); }
            finally
            {
                sw.Stop();
                var elapsed = FormatElapsed(sw.Elapsed);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsSynthesisRunning  = false;
                    SynthesisStatusText = cancelled
                        ? "● 未启动"
                        : $"● 合成完成（耗时 {elapsed}）";
                });
                if (!cancelled) _log.Log("合成", $"批次完成，耗时 {elapsed}");
            }
        });
        await Task.CompletedTask;
    }

}
