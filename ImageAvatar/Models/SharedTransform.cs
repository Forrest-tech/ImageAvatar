using SkiaSharp;

namespace ImageAvatar.Models;

/// <summary>
/// Holds a single SKMatrix shared across all ComparisonCanvas instances so that
/// pan and zoom applied to one view are instantly reflected in the others.
/// </summary>
public sealed class SharedTransform
{
    private SKMatrix _matrix = SKMatrix.Identity;

    public SKMatrix Matrix
    {
        get => _matrix;
        set
        {
            _matrix = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    public void Reset() => Matrix = SKMatrix.Identity;
}
