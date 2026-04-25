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

    public static AppSettingsService Load()
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
        catch { /* corrupt file – use defaults */ }

        return new AppSettingsService();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsFile,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
