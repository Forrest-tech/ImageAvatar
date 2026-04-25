using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Services;
using Wpf.Ui.Appearance;

namespace ImageAvatar.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILocalizationService    _localization;
    private readonly IImageExtractionService _extraction;
    private readonly AppSettingsService      _settings;

    // ── Language / Theme ───────────────────────────────────────────────────
    [ObservableProperty] private string _selectedLanguage;
    [ObservableProperty] private int    _selectedThemeIndex;

    public IReadOnlyList<string> SupportedLanguages => _localization.SupportedLanguages;
    public IReadOnlyList<string> ThemeNames { get; } = ["Dark", "Light"];

    // ── Model ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _modelPath;
    [ObservableProperty] private bool   _isModelLoaded;
    [ObservableProperty] private string _modelStatus;
    [ObservableProperty] private bool   _isLoadingModel;

    public SettingsViewModel(
        ILocalizationService    localization,
        IImageExtractionService extraction,
        AppSettingsService      settings)
    {
        _localization = localization;
        _extraction   = extraction;
        _settings     = settings;

        _selectedLanguage   = localization.CurrentLanguage;
        _selectedThemeIndex = 0;
        _modelPath          = settings.ModelPath;
        _isModelLoaded      = extraction.IsModelLoaded;
        _modelStatus        = extraction.IsModelLoaded ? "✓ Loaded" : "Not loaded";
    }

    // ── Language command ───────────────────────────────────────────────────

    [RelayCommand]
    private void ApplyLanguage() => _localization.SetLanguage(SelectedLanguage);

    // ── Theme command ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ApplyTheme()
    {
        var theme = SelectedThemeIndex == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme);
    }

    // ── Model commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select U-2-Net ONNX Model",
            Filter = "ONNX Model (*.onnx)|*.onnx|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            ModelPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task LoadModelAsync()
    {
        if (string.IsNullOrWhiteSpace(ModelPath)) return;

        IsLoadingModel = true;
        ModelStatus    = "Loading…";

        try
        {
            await _extraction.LoadModelAsync(ModelPath);
            _settings.ModelPath = ModelPath;
            _settings.Save();
            IsModelLoaded = true;
            ModelStatus   = "✓ Loaded";
        }
        catch (Exception ex)
        {
            IsModelLoaded = false;
            ModelStatus   = $"✗ {ex.Message}";
        }
        finally
        {
            IsLoadingModel = false;
        }
    }
}
