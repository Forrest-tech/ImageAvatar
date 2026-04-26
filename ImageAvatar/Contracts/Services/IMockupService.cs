using ImageAvatar.Models;
using SkiaSharp;

namespace ImageAvatar.Contracts.Services;

public interface IMockupService
{
    /// <summary>
    /// Overlays <paramref name="pattern"/> onto <paramref name="template"/> using
    /// Multiply blending so fabric wrinkles and shadows remain visible.
    /// Caller is responsible for disposing the returned SKBitmap.
    /// </summary>
    Task<SKBitmap> GenerateMockupAsync(SKBitmap pattern, SKBitmap template, PrintZone? zone = null);
}
