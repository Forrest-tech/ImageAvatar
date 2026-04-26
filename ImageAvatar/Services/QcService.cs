using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.IO;

namespace ImageAvatar.Services;

public sealed class QcService : IQcService
{
    private readonly IStorageService _storage;

    private string ProductionReadyPath =>
        Path.Combine(_storage.RootPath, "Production_Ready");

    private string ReworkPath =>
        Path.Combine(_storage.RootPath, "Rework");

    private static readonly string[] SourceExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    public QcService(IStorageService storage) => _storage = storage;

    // ── Item discovery ─────────────────────────────────────────────────────

    public IReadOnlyList<QcItem> LoadItems(
        string mockupFolder,
        string patternFolder,
        string sourceFolder)
    {
        if (!Directory.Exists(mockupFolder))
            return [];

        var items = new List<QcItem>();

        foreach (var mockupPath in Directory.GetFiles(mockupFolder, "*.png",
                     SearchOption.TopDirectoryOnly))
        {
            var stem          = Path.GetFileNameWithoutExtension(mockupPath);
            var lastUnderscore = stem.LastIndexOf('_');
            var patternStem   = lastUnderscore > 0 ? stem[..lastUnderscore] : stem;

            var candidatePattern = Path.Combine(patternFolder, patternStem + ".png");
            var originalPath     = FindOriginal(sourceFolder, patternStem);

            items.Add(new QcItem
            {
                MockupPath   = mockupPath,
                PatternPath  = File.Exists(candidatePattern) ? candidatePattern : string.Empty,
                OriginalPath = originalPath ?? string.Empty
            });
        }

        return items;
    }

    // ── Decisions ──────────────────────────────────────────────────────────

    public async Task ApproveAsync(QcItem item)
    {
        Directory.CreateDirectory(ProductionReadyPath);
        var dest = Path.Combine(ProductionReadyPath, Path.GetFileName(item.MockupPath));
        await Task.Run(() => File.Move(item.MockupPath, dest, overwrite: true));
        item.OutputPath = dest;
        item.Status     = QcStatus.Approved;
        _storage.RefreshAll();
    }

    public async Task RejectAsync(QcItem item)
    {
        Directory.CreateDirectory(ReworkPath);
        var dest = Path.Combine(ReworkPath, Path.GetFileName(item.MockupPath));
        await Task.Run(() => File.Move(item.MockupPath, dest, overwrite: true));
        item.OutputPath = dest;
        item.Status     = QcStatus.Rejected;
        _storage.RefreshAll();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? FindOriginal(string folder, string stem)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var ext in SourceExtensions)
        {
            var path = Path.Combine(folder, stem + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
