using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using ImageAvatar.ViewModels;
using ImageAvatar.Views;
using ImageAvatar.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;

namespace ImageAvatar;

public partial class App : Application
{
    private readonly IHost _host;

    public static IServiceProvider Services => ((App)Current)._host.Services;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, builder) =>
                builder.SetBasePath(AppContext.BaseDirectory)
                       .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false))
            .ConfigureServices((ctx, services) =>
            {
                // Settings: code defaults < appsettings.json < %AppData%\settings.json
                var appSettings = AppSettingsService.LoadWithDefaults(ctx.Configuration);
                services.AddSingleton(appSettings);

                // Core services
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IStorageService, StorageService>();
                services.AddSingleton<IImageExtractionService, ImageExtractionService>();
                services.AddSingleton<IPipelineCoordinatorService, PipelineCoordinatorService>();
                services.AddSingleton<IMockupService, MockupService>();
                services.AddSingleton<IBatchMockupService, BatchMockupService>();
                services.AddSingleton<IQcService, QcService>();

                // ViewModels
                services.AddSingleton<MainWindowViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<BatchProcessorViewModel>();
                services.AddTransient<QcViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
                services.AddTransient<DashboardPage>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<BatchPage>();
                services.AddTransient<QcPage>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        localization.SetLanguage("zh-CN");

        // Auto-load model if path exists
        var settings   = _host.Services.GetRequiredService<AppSettingsService>();
        var extraction = _host.Services.GetRequiredService<IImageExtractionService>();
        if (System.IO.File.Exists(settings.ModelPath))
        {
            try { await extraction.LoadModelAsync(settings.ModelPath); }
            catch { /* model file may be corrupt – ignore, user can reload in Settings */ }
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
