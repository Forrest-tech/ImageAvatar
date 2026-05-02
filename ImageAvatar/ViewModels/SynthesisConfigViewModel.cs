using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Services;
using System.IO;

namespace ImageAvatar.ViewModels;

public partial class SynthesisConfigViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;

    [ObservableProperty] private string _templatesFolder;
    [ObservableProperty] private bool   _preserveAspectRatio;
    [ObservableProperty] private int    _outputQuality;
    [ObservableProperty] private string _statusText = string.Empty;

    public SynthesisConfigViewModel(AppSettingsService settings)
    {
        _settings            = settings;
        _templatesFolder     = settings.TemplatesFolder;
        _preserveAspectRatio = settings.PreserveAspectRatio;
        _outputQuality       = settings.OutputQuality;
    }

    [RelayCommand]
    private void BrowseTemplates()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "选择模板文件夹",
            InitialDirectory = Directory.Exists(TemplatesFolder) ? TemplatesFolder : string.Empty
        };
        if (dialog.ShowDialog() == true)
            TemplatesFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.TemplatesFolder     = TemplatesFolder;
        _settings.PreserveAspectRatio = PreserveAspectRatio;
        _settings.OutputQuality       = OutputQuality;
        _settings.Save();
        StatusText = "✓ 配置已保存";
    }
}
