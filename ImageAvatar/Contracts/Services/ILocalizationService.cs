namespace ImageAvatar.Contracts.Services;

public interface ILocalizationService
{
    string CurrentLanguage { get; }
    IReadOnlyList<string> SupportedLanguages { get; }
    void SetLanguage(string languageCode);
    event EventHandler<string> LanguageChanged;
}
