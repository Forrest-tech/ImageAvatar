using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;

namespace ImageAvatar.ViewModels;

public partial class MattingConfigViewModel : ObservableObject
{
    private readonly IImageExtractionService _extraction;
    private readonly AppSettingsService      _settings;

    [ObservableProperty] private string _modelPath;
    [ObservableProperty] private bool   _isModelLoaded;
    [ObservableProperty] private string _modelStatus  = string.Empty;
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private double _threshold;

    public MattingConfigViewModel(IImageExtractionService extraction, AppSettingsService settings)
    {
        _extraction    = extraction;
        _settings      = settings;
        _modelPath     = settings.ModelPath;
        _isModelLoaded = extraction.IsModelLoaded;
        _modelStatus   = extraction.IsModelLoaded ? "✓ 模型已加载" : "未加载";
        _threshold     = settings.MattingThreshold;
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "选择 U-2-Net ONNX 模型文件",
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
            await _extraction.LoadModelAsync(ModelPath);
            _settings.ModelPath = ModelPath;
            _settings.Save();
            IsModelLoaded = true;
            ModelStatus   = "✓ 模型已加载";
        }
        catch (Exception ex)
        {
            IsModelLoaded = false;
            ModelStatus   = $"✗ {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void Save()
    {
        _settings.MattingThreshold = Threshold;
        _settings.Save();
        ModelStatus = "✓ 配置已保存";
    }
}
