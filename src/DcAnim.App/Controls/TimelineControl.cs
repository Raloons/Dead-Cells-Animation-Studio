using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DcAnim.App.Controls;

/// <summary>
/// Bottom timeline: one cell per frame, current-frame highlight, hit-frame
/// marker, click/drag scrubbing. Pure view: the host owns playback state.
/// </summary>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<int> FrameCountProperty =
        AvaloniaProperty.Register<TimelineControl, int>(nameof(FrameCount));

    public static readonly StyledProperty<int> CurrentFrameProperty =
        AvaloniaProperty.Register<TimelineControl, int>(nameof(CurrentFrame), -1);

    public static readonly StyledProperty<int> HitFrameProperty =
        AvaloniaProperty.Register<TimelineControl, int>(nameof(HitFrame), -1);

    private static readonly IBrush CellBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46));
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x5C));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xBC));

    private bool _scrubbing;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(FrameCountProperty, CurrentFrameProperty, HitFrameProperty);
    }

    /// <summary>Raised with the scrub fraction in [0,1].</summary>
    public event Action<double>? ScrubRequested;

    public int FrameCount
    {
        get => GetValue(FrameCountProperty);
        set => SetValue(FrameCountProperty, Math.Max(0, value));
    }

    public int CurrentFrame
    {
        get => GetValue(CurrentFrameProperty);
        set => SetValue(CurrentFrameProperty, value);
    }

    public int HitFrame
    {
        get => GetValue(HitFrameProperty);
        set => SetValue(HitFrameProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        int n = FrameCount;
        if (n <= 0)
        {
            var text = new FormattedText("No frames", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, TextBrush);
            context.DrawText(text, new Point(8, (bounds.Height - text.Height) / 2));
            return;
        }

        double cellW = bounds.Width / n;
        double barH = Math.Max(18, bounds.Height - 22);
        double barY = 4;

        for (int i = 0; i < n; i++)
        {
            double x = i * cellW;
            var rect = new Rect(x + 1, barY, Math.Max(1, cellW - 2), barH);
            var brush = i == CurrentFrame ? CurrentBrush : i < CurrentFrame ? DoneBrush : CellBrush;
            context.FillRectangle(brush, rect);
            if (i == HitFrame)
                context.FillRectangle(HitBrush, new Rect(x + 1, barY, Math.Max(1, cellW - 2), 4));
        }

        // Playhead.
        if (CurrentFrame >= 0)
        {
            double x = (CurrentFrame + 1) * cellW;
            context.FillRectangle(Brushes.White, new Rect(x - 1, barY, 2, barH));
        }

        // Ruler labels every frame when roomy, else every k-th.
        int step = Math.Max(1, (int)Math.Ceiling(n * 28.0 / Math.Max(1, bounds.Width)));
        for (int i = 0; i < n; i += step)
        {
            var text = new FormattedText(i.ToString(), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 10, TextBrush);
            context.DrawText(text, new Point(i * cellW + 3, barY + barH + 3));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FrameCount <= 0)
            return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _scrubbing = true;
            e.Pointer.Capture(this);
            ScrubTo(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scrubbing && FrameCount > 0)
            ScrubTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _scrubbing = false;
    }

    private void ScrubTo(double x)
    {
        if (Bounds.Width <= 0)
            return;
        ScrubRequested?.Invoke(Math.Clamp(x / Bounds.Width, 0.0, 1.0));
    }
}
