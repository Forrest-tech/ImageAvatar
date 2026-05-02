using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text.Json;

namespace ImageAvatar.Services;

public class AppSettingsService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImageAvatar", "settings.json");

    public string ModelPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImageAvatar", "models", "u2net.onnx");

    public string WorkspaceRoot { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string TemplatesFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ImageAvatar", "templates");

    // ── Global Config ──────────────────────────────────────────────────────
    public bool   UseSpuFilename { get; set; } = false;
    public string SpuPrefix      { get; set; } = "N";

    // ── Matting Config ─────────────────────────────────────────────────────
    public double MattingThreshold { get; set; } = 0.5;

    // ── Synthesis Config ───────────────────────────────────────────────────
    public bool PreserveAspectRatio { get; set; } = true;
    public int  OutputQuality       { get; set; } = 95;

    // ── Gen Config ─────────────────────────────────────────────────────────
    public string GenApiEndpoint { get; set; } = "http://localhost:7860";
    public int    GenSteps       { get; set; } = 20;
    public double GenCfgScale    { get; set; } = 7.0;

    // ── Loaders ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads user settings from %AppData%, then applies deployment defaults from
    /// appsettings.json (via IConfiguration) for any path that is still at its
    /// code default (i.e. the user has never saved their own value).
    /// Priority: %AppData%\settings.json > appsettings.json > code defaults.
    /// </summary>
    public static AppSettingsService LoadWithDefaults(IConfiguration config)
    {
        bool userHasSaved = File.Exists(SettingsFile);
        var settings = Load();

        if (!userHasSaved)
        {
            // Apply POD deployment overrides from appsettings.json
            var section = config.GetSection("ImageAvatar");
            ApplyIfSet(section["WorkspaceRoot"],   v => settings.WorkspaceRoot   = v);
            ApplyIfSet(section["ModelPath"],       v => settings.ModelPath       = v);
            ApplyIfSet(section["TemplatesFolder"], v => settings.TemplatesFolder = v);
        }

        return settings;
    }

    private static AppSettingsService Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettingsService>(json)
                       ?? new AppSettingsService();
            }
        }
        catch { /* corrupt – use defaults */ }

        return new AppSettingsService();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsFile,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ApplyIfSet(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value)) apply(value);
    }
}
