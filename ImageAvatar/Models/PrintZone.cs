namespace ImageAvatar.Models;

/// <summary>
/// Relative coordinates (0–1) describing where a pattern is placed on a garment template.
/// CenterY=0.45 places the print area slightly above centre, matching the chest print zone.
/// </summary>
public record PrintZone(float CenterX, float CenterY, float WidthRatio, float HeightRatio)
{
    public static PrintZone Default { get; } = new(0.5f, 0.45f, 0.62f, 0.62f);
}
