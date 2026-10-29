using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DcAnim.App.Controls;

/// <summary>
/// Center viewport: checkerboard background with the current frame centered,
/// scaled by <see cref="Zoom"/> using nearest-neighbor sampling (no pixel-art blur).
/// </summary>
public sealed class PreviewControl : Control
{
    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<PreviewControl, Bitmap?>(nameof(Frame));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PreviewControl, double>(nameof(Zoom), 3.0);

    public static readonly StyledProperty<bool> ShowCheckerProperty =
        AvaloniaProperty.Register<PreviewControl, bool>(nameof(ShowChecker), true);

    public static readonly StyledProperty<Vector> PanProperty =
        AvaloniaProperty.Register<PreviewControl, Vector>(nameof(Pan), new Vector(0, 0));

    private static readonly IBrush CheckLight = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
    private static readonly IBrush CheckDark = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x1E));
    private static readonly IBrush OverlayPen = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5));
    private static readonly IBrush OverlayFill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC));

    static PreviewControl()
    {
        AffectsRender<PreviewControl>(FrameProperty, ZoomProperty, ShowCheckerProperty, PanProperty,
            SelectionRectProperty, PivotMarkerProperty);
    }

    public PreviewControl()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public Bitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, 1.0, 8.0));
    }

    public bool ShowChecker
    {
        get => GetValue(ShowCheckerProperty);
        set => SetValue(ShowCheckerProperty, value);
    }

    public Vector Pan
    {
        get => GetValue(PanProperty);
        set => SetValue(PanProperty, value);
    }

    /// <summary>Editor-only selection rectangle in canvas pixels (never exported).</summary>
    public Rect? SelectionRect
    {
        get => GetValue(SelectionRectProperty);
        set => SetValue(SelectionRectProperty, value);
    }

    public static readonly StyledProperty<Rect?> SelectionRectProperty =
        AvaloniaProperty.Register<PreviewControl, Rect?>(nameof(SelectionRect));

    /// <summary>Editor-only pivot marker in canvas pixels (never exported).</summary>
    public Point? PivotMarker
    {
        get => GetValue(PivotMarkerProperty);
        set => SetValue(PivotMarkerProperty, value);
    }

    public static readonly StyledProperty<Point?> PivotMarkerProperty =
        AvaloniaProperty.Register<PreviewControl, Point?>(nameof(PivotMarker));

    /// <summary>When true, left-drag moves the selection instead of panning.</summary>
    public bool MoveEnabled { get; set; }

    /// <summary>Raised while moving the selection (delta in screen pixels).</summary>
    public event Action<Vector>? MoveDelta;
    public event Action? MoveStarted;
    public event Action? MoveFinished;

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (ShowChecker)
        {
            // Checkerboard.
            const double cell = 14.0;
            bool flip = false;
            for (double y = 0; y < bounds.Height; y += cell, flip = !flip)
            {
                bool alt = flip;
                for (double x = 0; x < bounds.Width; x += cell, alt = !alt)
                {
                    context.FillRectangle(alt ? CheckLight : CheckDark,
                        new Rect(x, y, Math.Min(cell, bounds.Width - x), Math.Min(cell, bounds.Height - y)));
                }
            }
        }
        else
        {
            context.FillRectangle(CheckDark, bounds);
        }

        var frame = Frame;
        if (frame is null)
        {
            var text = new FormattedText("No animation loaded", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA8)));
            context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
            return;
        }

        double w = frame.PixelSize.Width * Zoom;
        double h = frame.PixelSize.Height * Zoom;
        var dest = new Rect((bounds.Width - w) / 2 + Pan.X, (bounds.Height - h) / 2 + Pan.Y, w, h);
        var source = new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        context.DrawImage(frame, source, dest);

        // Editor-only selection overlay (never exported).
        if (SelectionRect is { } sel)
        {
            var r = new Rect(
                dest.X + sel.X * Zoom, dest.Y + sel.Y * Zoom,
                sel.Width * Zoom, sel.Height * Zoom);
            context.DrawRectangle(new Pen(OverlayPen, 1), r);
            // Corner ticks.
            foreach (var c in new[] { r.TopLeft, new Point(r.Right, r.Top), new Point(r.Left, r.Bottom), r.BottomRight })
                context.FillRectangle(OverlayFill, new Rect(c.X - 1.5, c.Y - 1.5, 3, 3));
        }
        if (PivotMarker is { } pivot)
        {
            var p = new Point(dest.X + pivot.X * Zoom, dest.Y + pivot.Y * Zoom);
            context.DrawLine(new Pen(OverlayPen, 1), new Point(p.X - 6, p.Y), new Point(p.X + 6, p.Y));
            context.DrawLine(new Pen(OverlayPen, 1), new Point(p.X, p.Y - 6), new Point(p.X, p.Y + 6));
        }
    }

    private Point? _panStart;
    private Vector _panOrigin;
    private bool _moving;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _panStart = e.GetPosition(this);
            _panOrigin = Pan;
            _moving = false;
            if (MoveEnabled)
                MoveStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panStart is null)
            return;
        var delta = e.GetPosition(this) - _panStart.Value;
        if (!_moving && (delta.X * delta.X + delta.Y * delta.Y <= 16))
            return;
        _moving = true;
        if (MoveEnabled)
            MoveDelta?.Invoke(delta);
        else
            Pan = _panOrigin + delta;
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panStart is null)
            return;
        bool wasMoving = _moving;
        _panStart = null;
        _moving = false;
        if (MoveEnabled && wasMoving)
            MoveFinished?.Invoke();
    }
}
