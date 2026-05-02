using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Services;

namespace ImageAvatar.ViewModels;

public partial class GlobalConfigViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;

    [ObservableProperty] private bool   _useSpuFilename;
    [ObservableProperty] private string _spuPrefix = "N";

    public GlobalConfigViewModel(AppSettingsService settings)
    {
        _settings      = settings;
        _useSpuFilename = settings.UseSpuFilename;
        _spuPrefix      = settings.SpuPrefix;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.UseSpuFilename = UseSpuFilename;
        _settings.SpuPrefix      = SpuPrefix;
        _settings.Save();
    }
}
