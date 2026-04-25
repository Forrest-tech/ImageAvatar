using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using Wpf.Ui.Appearance;

namespace ImageAvatar.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    private int _selectedThemeIndex;

    public IReadOnlyList<string> SupportedLanguages => _localization.SupportedLanguages;

    public IReadOnlyList<string> ThemeNames { get; } = ["Dark", "Light"];

    public SettingsViewModel(ILocalizationService localization)
    {
        _localization = localization;
        _selectedLanguage = localization.CurrentLanguage;
        _selectedThemeIndex = 0; // Dark by default
    }

    [RelayCommand]
    private void ApplyLanguage()
    {
        _localization.SetLanguage(SelectedLanguage);
    }

    [RelayCommand]
    private void ApplyTheme()
    {
        var theme = SelectedThemeIndex == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme);
    }
}
