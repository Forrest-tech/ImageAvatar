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
            .ConfigureServices((_, services) =>
            {
                // Core services
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IStorageService, StorageService>();

                // ViewModels
                services.AddSingleton<MainWindowViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<SettingsViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
                services.AddTransient<DashboardPage>();
                services.AddTransient<SettingsPage>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Initialize default language (zh-CN)
        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        localization.SetLanguage("zh-CN");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
