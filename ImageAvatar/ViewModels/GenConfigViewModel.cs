using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Services;

namespace ImageAvatar.ViewModels;

public partial class GenConfigViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;

    [ObservableProperty] private string _apiEndpoint;
    [ObservableProperty] private string _apiKey      = string.Empty;
    [ObservableProperty] private int    _steps;
    [ObservableProperty] private double _cfgScale;
    [ObservableProperty] private int    _outputWidth  = 512;
    [ObservableProperty] private int    _outputHeight = 512;
    [ObservableProperty] private string _statusText   = string.Empty;
    [ObservableProperty] private bool   _isTesting;

    public GenConfigViewModel(AppSettingsService settings)
    {
        _settings    = settings;
        _apiEndpoint = settings.GenApiEndpoint;
        _steps       = settings.GenSteps;
        _cfgScale    = settings.GenCfgScale;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting  = true;
        StatusText = "正在连接…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync(ApiEndpoint + "/sdapi/v1/sd-models");
            StatusText = resp.IsSuccessStatusCode ? "✓ 连接成功" : $"✗ HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ {ex.Message}";
        }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private void Save()
    {
        _settings.GenApiEndpoint = ApiEndpoint;
        _settings.GenSteps       = Steps;
        _settings.GenCfgScale    = CfgScale;
        _settings.Save();
        StatusText = "✓ 配置已保存";
    }
}
