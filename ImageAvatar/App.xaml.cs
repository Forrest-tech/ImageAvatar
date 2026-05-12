using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using ImageAvatar.ViewModels;
using ImageAvatar.Views;
using ImageAvatar.Views.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Windows;
using Wpf.Ui;

namespace ImageAvatar;

public partial class App : Application
{
    private readonly IHost _host;

    public static IServiceProvider Services => ((App)Current)._host.Services;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    static App()
    {
        // Ensure OpenCvSharp and ONNX native DLLs are findable.
        // For AnyCPU framework-dependent builds, NuGet places them in
        // runtimes/win-x64/native/ — AddDllDirectory registers that path
        // so [DllImport] resolution works without a full publish.
        var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        var nativeDir = System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
        if (System.IO.Directory.Exists(nativeDir))
            AddDllDirectory(nativeDir);
    }

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, builder) =>
                builder.SetBasePath(AppContext.BaseDirectory)
                       .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false))
            .ConfigureServices((ctx, services) =>
            {
                // Settings
                var appSettings = AppSettingsService.LoadWithDefaults(ctx.Configuration);
                services.AddSingleton(appSettings);

                // ── Core services ──────────────────────────────────────────
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IStorageService,      StorageService>();
                services.AddSingleton<IImageExtractionService, ImageExtractionService>();
                services.AddSingleton<IPipelineCoordinatorService, PipelineCoordinatorService>();
                services.AddSingleton<IMockupService,       MockupService>();
                services.AddSingleton<IBatchMockupService,  BatchMockupService>();
                services.AddSingleton<IQcService,           QcService>();
                services.AddSingleton<ILogService,          LogService>();
                services.AddSingleton<ICropService,         CropService>();
                services.AddSingleton<IMattingService,      MattingService>();
                services.AddSingleton<IPromptService,       PromptService>();
                services.AddSingleton<IGenService,          GenService>();

                // ── Navigation (WPF-UI) ────────────────────────────────────
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IPageService,       NavigationPageService>();

                // ── ViewModels ─────────────────────────────────────────────
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<GlobalConfigViewModel>();
                services.AddSingleton<CropConfigViewModel>();
                services.AddSingleton<PromptConfigViewModel>();
                services.AddSingleton<GenConfigViewModel>();
                services.AddSingleton<MattingConfigViewModel>();
                services.AddSingleton<SynthesisConfigViewModel>();
                services.AddSingleton<ExcelGenViewModel>();
                services.AddSingleton<ConsoleViewModel>();
                // Legacy (kept for potential future use)
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<BatchProcessorViewModel>();
                services.AddSingleton<QcViewModel>();

                // ── Views ──────────────────────────────────────────────────
                services.AddSingleton<MainWindow>();
                services.AddSingleton<DashboardPage>();
                services.AddSingleton<GlobalConfigPage>();
                services.AddSingleton<CropConfigPage>();
                services.AddSingleton<PromptConfigPage>();
                services.AddSingleton<GenConfigPage>();
                services.AddSingleton<MattingConfigPage>();
                services.AddSingleton<SynthesisConfigPage>();
                services.AddSingleton<ExcelGenPage>();
                services.AddSingleton<ConsolePage>();
                // Legacy
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<BatchPage>();
                services.AddSingleton<QcPage>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[Dispatcher {DateTime.Now:HH:mm:ss}] {ex.Exception}\n\n");
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[AppDomain {DateTime.Now:HH:mm:ss}] {ex.ExceptionObject}\n\n");
        };

        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[Task {DateTime.Now:HH:mm:ss}] {ex.Exception}\n\n");
            ex.SetObserved();
        };

        await _host.StartAsync();

        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        localization.SetLanguage("zh-CN");

        var log = _host.Services.GetRequiredService<ILogService>();
        log.Log("系统", "ImageAvatar 已启动");

        var settings   = _host.Services.GetRequiredService<AppSettingsService>();
        var extraction = _host.Services.GetRequiredService<IImageExtractionService>();
        if (System.IO.File.Exists(settings.ModelPath))
        {
            try
            {
                await extraction.LoadModelAsync(settings.ModelPath);
                log.Log("模型", $"已自动加载：{settings.ModelPath}");
            }
            catch (Exception ex)
            {
                log.Log("模型", $"加载失败：{ex.Message}");
            }
        }

        var matting = _host.Services.GetRequiredService<IMattingService>();
        if (!string.IsNullOrWhiteSpace(settings.MattingModelPath) &&
            System.IO.File.Exists(settings.MattingModelPath))
        {
            try
            {
                await matting.LoadModelAsync(settings.MattingModelPath);
                log.Log("抠图", $"已自动加载抠图模型：{settings.MattingModelPath}");
            }
            catch (Exception ex)
            {
                log.Log("抠图", $"加载失败：{ex.Message}（将使用 U-2-Net 备用引擎）");
            }
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _host.Services.GetRequiredService<IPipelineCoordinatorService>().Stop();
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
