using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageAvatar.ViewModels;

public partial class PromptConfigViewModel : ObservableObject
{
    [ObservableProperty] private string _promptTemplate  = "A garment with {pattern} design, product photography, white background";
    [ObservableProperty] private string _negativePrompt  = "blurry, low quality, text, watermark, shadow";
    [ObservableProperty] private string _stylePreset     = "product-photography";
    [ObservableProperty] private int    _seed            = -1;
    [ObservableProperty] private string _statusText      = string.Empty;

    [RelayCommand]
    private void Save()
    {
        StatusText = "✓ 配置已保存";
    }
}
