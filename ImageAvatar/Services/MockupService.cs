using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using SkiaSharp;

namespace ImageAvatar.Services;

/// <summary>
/// Composites a transparent-background pattern PNG onto a garment template
/// using SKBlendMode.Multiply so fabric shadows and wrinkles remain visible.
///
/// Multiply formula (per channel, premultiplied):
///   result = src * dst + src*(1-dst.a) + dst*(1-src.a)
/// When src.a = 0 (transparent pattern pixel) → result = dst (template unchanged).
/// When src.a = 1 over a white (1.0) template pixel → result = src color.
/// When src.a = 1 over a grey (0.7) shadow pixel → result = src * 0.7 (shadow visible).
/// </summary>
public sealed class MockupService : IMockupService
{
    public Task<SKBitmap> GenerateMockupAsync(
        SKBitmap pattern,
        SKBitmap template,
        PrintZone? zone = null)
    {
        zone ??= PrintZone.Default;
        return Task.Run(() => Compose(pattern, template, zone));
    }

    private static SKBitmap Compose(SKBitmap pattern, SKBitmap template, PrintZone zone)
    {
        int w = template.Width, h = template.Height;

        var output = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);

        // White background ensures Multiply has a well-defined base for templates
        // that have a transparent or semi-transparent border.
        canvas.Clear(SKColors.White);

        // 1 – Draw the garment template as the base layer
        canvas.DrawBitmap(template, 0, 0);

        // 2 – Compute the print-zone rectangle in pixels
        float zoneW = w * zone.WidthRatio;
        float zoneH = h * zone.HeightRatio;
        float zoneX = w * zone.CenterX - zoneW / 2f;
        float zoneY = h * zone.CenterY - zoneH / 2f;

        // 3 – Scale pattern to fit the zone while preserving aspect ratio (letterbox)
        float scale = Math.Min(zoneW / pattern.Width, zoneH / pattern.Height);
        float destW = pattern.Width  * scale;
        float destH = pattern.Height * scale;
        float destX = zoneX + (zoneW - destW) / 2f;
        float destY = zoneY + (zoneH - destH) / 2f;

        var destRect = new SKRect(destX, destY, destX + destW, destY + destH);

        // 4 – Overlay pattern with Multiply blend; SoftLight can be swapped in for a
        //     more subtle look on coloured garments.
        using var paint = new SKPaint
        {
            BlendMode     = SKBlendMode.Multiply,
            FilterQuality = SKFilterQuality.High,
            IsAntialias   = true
        };
        canvas.DrawBitmap(pattern, destRect, paint);
        canvas.Flush();

        return output;
    }
}
