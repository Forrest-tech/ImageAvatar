using ImageAvatar.Models;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageAvatar.Controls;

/// <summary>
/// A WPF UserControl wrapping SKElement that renders a bitmap with linked
/// zoom and pan. All instances that share the same <see cref="SharedTransform"/>
/// move in perfect sync when the user pans or zooms any one of them.
///
/// Zoom  : mouse wheel, centred on cursor.
/// Pan   : left-button drag.
/// Reset : call Transform.Reset() from outside (e.g. a toolbar button).
/// </summary>
public sealed class ComparisonCanvas : System.Windows.Controls.UserControl
{
    private readonly SKElement _element;
    private bool  _isDragging;
    private Point _lastPos;

    // ── Dependency Properties ──────────────────────────────────────────────

    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(SKBitmap), typeof(ComparisonCanvas),
            new PropertyMetadata(null, (d, _) => ((ComparisonCanvas)d).Repaint()));

    public static readonly DependencyProperty OverlayImageProperty =
        DependencyProperty.Register(nameof(OverlayImage), typeof(SKBitmap), typeof(ComparisonCanvas),
            new PropertyMetadata(null, (d, _) => ((ComparisonCanvas)d).Repaint()));

    public static readonly DependencyProperty OverlayOpacityProperty =
        DependencyProperty.Register(nameof(OverlayOpacity), typeof(double), typeof(ComparisonCanvas),
            new PropertyMetadata(0.5, (d, _) => ((ComparisonCanvas)d).Repaint()));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(ComparisonCanvas),
            new PropertyMetadata(string.Empty, (d, _) => ((ComparisonCanvas)d).Repaint()));

    public static readonly DependencyProperty TransformProperty =
        DependencyProperty.Register(nameof(Transform), typeof(SharedTransform), typeof(ComparisonCanvas),
            new PropertyMetadata(null, OnTransformChanged));

    public SKBitmap? Image
    {
        get => (SKBitmap?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public SKBitmap? OverlayImage
    {
        get => (SKBitmap?)GetValue(OverlayImageProperty);
        set => SetValue(OverlayImageProperty, value);
    }

    public double OverlayOpacity
    {
        get => (double)GetValue(OverlayOpacityProperty);
        set => SetValue(OverlayOpacityProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public SharedTransform? Transform
    {
        get => (SharedTransform?)GetValue(TransformProperty);
        set => SetValue(TransformProperty, value);
    }

    // ── Transform change – subscribe / unsubscribe ────────────────────────

    private static void OnTransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ComparisonCanvas)d;
        if (e.OldValue is SharedTransform old) old.Changed -= self.OnSharedTransformChanged;
        if (e.NewValue is SharedTransform @new) @new.Changed += self.OnSharedTransformChanged;
        self.Repaint();
    }

    private void OnSharedTransformChanged(object? sender, EventArgs e) => Repaint();

    // ── Constructor ────────────────────────────────────────────────────────

    public ComparisonCanvas()
    {
        _element = new SKElement { Focusable = false };
        _element.PaintSurface += OnPaintSurface;
        Content = _element;

        Background       = Brushes.Transparent;
        Focusable        = true;
        FocusVisualStyle = null;
        Cursor           = Cursors.Cross;

        MouseWheel          += OnMouseWheel;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp   += OnMouseUp;
        MouseMove           += OnMouseMove;
    }

    // ── Mouse – zoom ───────────────────────────────────────────────────────

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var t = Transform;
        if (t is null) return;

        float scale = e.Delta > 0 ? 1.12f : 1f / 1.12f;
        var   pos   = e.GetPosition(_element);
        float cx    = (float)pos.X;
        float cy    = (float)pos.Y;

        // Zoom centred on cursor:  T_back · Scale · T_to_origin · M_current
        var zoom = SKMatrix.CreateTranslation(-cx, -cy);
        zoom = zoom.PostConcat(SKMatrix.CreateScale(scale, scale));
        zoom = zoom.PostConcat(SKMatrix.CreateTranslation(cx, cy));

        t.Matrix = t.Matrix.PostConcat(zoom);
        e.Handled = true;
    }

    // ── Mouse – pan ────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastPos    = e.GetPosition(this);
        Mouse.Capture(this);
        Cursor = Cursors.Hand;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        Mouse.Capture(null);
        Cursor = Cursors.Cross;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var t = Transform;
        if (t is null) return;

        var pos   = e.GetPosition(this);
        var delta = pos - _lastPos;
        _lastPos  = pos;

        // Screen-space translation appended after the current matrix
        t.Matrix = t.Matrix.PostConcat(
            SKMatrix.CreateTranslation((float)delta.X, (float)delta.Y));
    }

    // ── Painting ───────────────────────────────────────────────────────────

    private void Repaint() => _element.InvalidateVisual();

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(28, 28, 30)); // near-black background

        var img = Image;
        if (img is null)
        {
            DrawPlaceholder(canvas, e.Info);
            DrawCaption(canvas, Caption);
            return;
        }

        // Use shared matrix; when identity (initial / after reset) compute a
        // centre-fit so each canvas shows its image fully from the start.
        var matrix = Transform?.Matrix ?? SKMatrix.Identity;
        if (matrix.IsIdentity && e.Info.Width > 0 && img.Width > 0)
        {
            float s  = Math.Min((float)e.Info.Width  / img.Width,
                                (float)e.Info.Height / img.Height) * 0.92f;
            float ox = ((float)e.Info.Width  - img.Width  * s) / 2f;
            float oy = ((float)e.Info.Height - img.Height * s) / 2f;
            matrix   = SKMatrix.CreateScaleTranslation(s, s, ox, oy);
        }

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(img, 0, 0);

        // Overlay (Overlay Mode only): PNG on top of original with adjustable opacity
        var overlay = OverlayImage;
        if (overlay is not null)
        {
            using var overlayPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(OverlayOpacity * 255)),
            };
            canvas.DrawBitmap(overlay, 0, 0, overlayPaint);
        }

        canvas.ResetMatrix();
        DrawCaption(canvas, Caption);
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKImageInfo info)
    {
        using var paint = new SKPaint { Color = new SKColor(70, 70, 75), IsAntialias = true };
        canvas.DrawCircle(info.Width / 2f, info.Height / 2f, 20, paint);
    }

    private static void DrawCaption(SKCanvas canvas, string caption)
    {
        if (string.IsNullOrEmpty(caption)) return;

        using var textPaint = new SKPaint
        {
            Color       = SKColors.White,
            TextSize    = 11,
            IsAntialias = true
        };
        float tw = textPaint.MeasureText(caption);

        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 175) };
        canvas.DrawRect(new SKRect(8, 8, tw + 22, 27), bgPaint);
        canvas.DrawText(caption, 15, 23, textPaint);
    }
}
