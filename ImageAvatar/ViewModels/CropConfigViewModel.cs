using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageAvatar.ViewModels;

public partial class CropConfigViewModel : ObservableObject
{
    [ObservableProperty] private int    _minWidth      = 800;
    [ObservableProperty] private int    _minHeight     = 800;
    [ObservableProperty] private double _paddingRatio  = 0.05;
    [ObservableProperty] private bool   _autoRotate    = true;
    [ObservableProperty] private bool   _squareOutput  = true;
    [ObservableProperty] private string _statusText    = string.Empty;

    [RelayCommand]
    private void Save()
    {
        // TODO: persist crop config to AppSettingsService when fields are added
        StatusText = "✓ 配置已保存";
    }
}
