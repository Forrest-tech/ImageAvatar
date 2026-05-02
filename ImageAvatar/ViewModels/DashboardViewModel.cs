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
    private readonly IPipelineCoordinatorService _matting;
    private readonly IBatchMockupService         _synthesis;
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
    [ObservableProperty] private bool _isMattingRunning;
    [ObservableProperty] private bool _isSynthesisRunning;

    private CancellationTokenSource? _synthesisCts;

    public DashboardViewModel(
        IStorageService             storage,
        IPipelineCoordinatorService matting,
        IBatchMockupService         synthesis,
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
        if (dialog.ShowDialog() != true) return;

        RootPath                 = dialog.FolderName;
        _storage.RootPath        = RootPath;
        _settings.WorkspaceRoot  = RootPath;
        _settings.Save();
        _storage.RefreshAll();
        _log.Log("工作区", $"已切换至 {RootPath}");
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
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsCropRunning  = false;
                    CropStatusText = cancelled ? "● 未启动" : "● 裁剪完成";
                });
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

    // ── 自动抠图 (pipeline coordinator) ────────────────────────────────────

    [RelayCommand]
    private async Task ToggleMattingAsync()
    {
        if (IsMattingRunning)
        {
            await Task.Run(() => _matting.Stop());
            IsMattingRunning = false;
            _log.Log("抠图", "服务已停止");
        }
        else
        {
            await Task.Run(() => _matting.Start());
            IsMattingRunning = true;
            _log.Log("抠图", "服务已启动");
        }
    }

    // ── 自动合成 (batch mockup) ────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleSynthesisAsync()
    {
        if (IsSynthesisRunning)
        {
            _synthesisCts?.Cancel();
            _synthesisCts    = null;
            IsSynthesisRunning = false;
            _log.Log("合成", "服务已停止");
            return;
        }

        _synthesisCts      = new CancellationTokenSource();
        IsSynthesisRunning = true;
        _log.Log("合成", "服务已启动");

        var inputFolder     = Path.Combine(RootPath, "50_成品队列");
        var outputFolder    = Path.Combine(RootPath, "51_成品完成");
        var templatesFolder = _settings.TemplatesFolder;
        var cts             = _synthesisCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await _synthesis.RunAsync(inputFolder, outputFolder, templatesFolder, ct: cts.Token);
                _log.Log("合成", "批次完成");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Log("合成", $"异常: {ex.Message}"); }
            finally
            {
                Application.Current?.Dispatcher.Invoke(() => IsSynthesisRunning = false);
            }
        });

        await Task.CompletedTask;
    }
}
