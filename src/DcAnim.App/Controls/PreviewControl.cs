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
    private static readonly IBrush PastWash = new SolidColorBrush(Color.FromArgb(70, 0x7A, 0x90, 0xB0));
    private static readonly IBrush FutureWash = new SolidColorBrush(Color.FromArgb(70, 0xB0, 0x90, 0x5A));

    private static void DrawOnionGhost(DrawingContext context, Bitmap ghost, Rect dest, double opacity, IBrush wash)
    {
        // Same dest rect as the base frame (identical canvas): pixel-crisp
        // alignment for free, including holds and reversed frames.
        var source = new Rect(0, 0, ghost.PixelSize.Width, ghost.PixelSize.Height);
        using (context.PushOpacity(opacity))
            context.DrawImage(ghost, source, dest);
        context.FillRectangle(wash, dest);
    }

    static PreviewControl()
    {
        AffectsRender<PreviewControl>(FrameProperty, ZoomProperty, ShowCheckerProperty, PanProperty,
            SelectionRectProperty, PivotMarkerProperty,
            OnionPreviousProperty, OnionNextProperty, OnionOpacityProperty);
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

    /// <summary>
    /// Onion-skin neighbors (preview-only, never exported): previous frames
    /// oldest-first, next frames nearest-first. Same canvas size as
    /// <see cref="Frame"/> (composed sequence frames), drawn underneath it.
    /// </summary>
    public IList<Bitmap>? OnionPrevious
    {
        get => GetValue(OnionPreviousProperty);
        set => SetValue(OnionPreviousProperty, value);
    }

    public static readonly StyledProperty<IList<Bitmap>?> OnionPreviousProperty =
        AvaloniaProperty.Register<PreviewControl, IList<Bitmap>?>(nameof(OnionPrevious));

    public IList<Bitmap>? OnionNext
    {
        get => GetValue(OnionNextProperty);
        set => SetValue(OnionNextProperty, value);
    }

    public static readonly StyledProperty<IList<Bitmap>?> OnionNextProperty =
        AvaloniaProperty.Register<PreviewControl, IList<Bitmap>?>(nameof(OnionNext));

    /// <summary>Ghost opacity for onion skins (0.15..0.6).</summary>
    public double OnionOpacity
    {
        get => GetValue(OnionOpacityProperty);
        set => SetValue(OnionOpacityProperty, value);
    }

    public static readonly StyledProperty<double> OnionOpacityProperty =
        AvaloniaProperty.Register<PreviewControl, double>(nameof(OnionOpacity), 0.35);

    /// <summary>Raised while moving the selection (delta in screen pixels).</summary>
    public event Action<Vector>? MoveDelta;
    public event Action? MoveStarted;
    public event Action? MoveFinished;
    /// <summary>Raised when an in-progress move is lost (e.g. pointer capture
    /// stolen); hosts must abort any open coalesced undo group.</summary>
    public event Action? MoveCancelled;

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

        // Onion skins (preview-only): neighbors beneath the current frame.
        // Past = cool slate, future = warm sand — restrained and
        // distinguishable without touching the approved palette.
        double ghost = Math.Clamp(OnionOpacity, 0.0, 0.6);
        if (ghost > 0)
        {
            if (OnionPrevious is { Count: > 0 })
            {
                foreach (var prev in OnionPrevious)
                    DrawOnionGhost(context, prev, dest, ghost, PastWash);
            }
            if (OnionNext is { Count: > 0 })
            {
                foreach (var next in OnionNext)
                    DrawOnionGhost(context, next, dest, ghost, FutureWash);
            }
        }
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
    private bool _moveArmed;
    private bool _moveStarted;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_panStart is not null)
        {
            bool wasActive = _moving || _moveArmed;
            ResetPointerState();
            if (wasActive)
                MoveCancelled?.Invoke();
            return;
        }
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _panStart = e.GetPosition(this);
            _panOrigin = Pan;
            _moving = false;
            _moveArmed = MoveEnabled;
            _moveStarted = false;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        bool wasActive = _panStart is not null && (_moving || _moveArmed);
        ResetPointerState();
        if (wasActive)
            MoveCancelled?.Invoke();
    }

    private void ResetPointerState()
    {
        _panStart = null;
        _moving = false;
        _moveArmed = false;
        _moveStarted = false;
        InvalidateVisual();
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
        if (!MoveEnabled)
        {
            Pan = _panOrigin + delta;
            return;
        }
        if (_moveArmed)
        {
            // Begin the coalesced undo group only once real movement starts.
            _moveArmed = false;
            _moveStarted = true;
            MoveStarted?.Invoke();
        }
        MoveDelta?.Invoke(delta);
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panStart is null)
            return;
        bool wasStarted = _moveStarted;
        ResetPointerState();
        if (wasStarted)
            MoveFinished?.Invoke();
    }
}
