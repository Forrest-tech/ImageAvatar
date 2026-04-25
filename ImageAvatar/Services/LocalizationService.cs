using ImageAvatar.Contracts.Services;
using System.Windows;

namespace ImageAvatar.Services;

public class LocalizationService : ILocalizationService
{
    private static readonly string[] _supported = ["zh-CN", "en-US", "fr-FR", "zh-HK"];

    public string CurrentLanguage { get; private set; } = "zh-CN";
    public IReadOnlyList<string> SupportedLanguages => _supported;
    public event EventHandler<string>? LanguageChanged;

    public void SetLanguage(string languageCode)
    {
        if (!_supported.Contains(languageCode))
            throw new ArgumentException($"Unsupported language: {languageCode}");

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/ImageAvatar;component/Resources/Strings/Strings.{languageCode}.xaml",
                UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;

        // Remove any previously loaded Strings dictionary
        var existing = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Resources/Strings/Strings.") == true);
        if (existing is not null)
            merged.Remove(existing);

        merged.Add(dict);

        CurrentLanguage = languageCode;
        LanguageChanged?.Invoke(this, languageCode);
    }
}
