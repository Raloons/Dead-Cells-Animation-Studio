using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DcAnim.Core.Playback;

namespace DcAnim.App.Controls;

/// <summary>
/// Single-animation filmstrip track: time ruler, frame thumbnails, selected
/// frame highlight, hit-frame marker, playhead, click/drag scrubbing.
/// Sized by the host (<see cref="TrackWidth"/>); draws the ruler on top and
/// the frame lane below. Designed so future multi-clip tracks can reuse it.
/// </summary>
public sealed class TrackControl : Control
{
    public static readonly StyledProperty<IList<Bitmap>?> FramesProperty =
        AvaloniaProperty.Register<TrackControl, IList<Bitmap>?>(nameof(Frames));

    public static readonly StyledProperty<int> CurrentFrameProperty =
        AvaloniaProperty.Register<TrackControl, int>(nameof(CurrentFrame), -1);

    public static readonly StyledProperty<int> HitFrameProperty =
        AvaloniaProperty.Register<TrackControl, int>(nameof(HitFrame), -1);

    public static readonly StyledProperty<double> PixelsPerFrameProperty =
        AvaloniaProperty.Register<TrackControl, double>(nameof(PixelsPerFrame), 32.0);

    public static readonly StyledProperty<double> FpsProperty =
        AvaloniaProperty.Register<TrackControl, double>(nameof(Fps), 30.0);

    public const double RulerHeight = 26.0;
    public const double LaneHeight = 56.0;
    public const double LaneTop = RulerHeight + 4;

    private static readonly IBrush RulerBrush = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B));
    private static readonly IBrush CellBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
    private static readonly IBrush PlayedBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x34));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3D));
    private static readonly IPen CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5)), 1.5);
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0x97, 0x3F));
    private static readonly IBrush MinorGridBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x27));
    private static readonly IBrush MajorGridBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x31, 0x38));
    private static readonly IBrush LaneSeparatorBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x7F));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)), 1.5);

    private bool _scrubbing;

    static TrackControl()
    {
        AffectsRender<TrackControl>(FramesProperty, CurrentFrameProperty, HitFrameProperty, PixelsPerFrameProperty, FpsProperty);
    }

    /// <summary>Raised with the scrub fraction in [0,1].</summary>
    public event Action<double>? ScrubRequested;

    public IList<Bitmap>? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
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

    public double PixelsPerFrame
    {
        get => GetValue(PixelsPerFrameProperty);
        set => SetValue(PixelsPerFrameProperty, Math.Clamp(value, 8.0, 96.0));
    }

    public double Fps
    {
        get => GetValue(FpsProperty);
        set => SetValue(FpsProperty, value > 0 ? value : 30.0);
    }

    public int FrameCount => Frames?.Count ?? 0;

    public double TrackWidth => TimelineMath.TrackWidth(FrameCount, PixelsPerFrame) + 8;

    public override void Render(DrawingContext context)
    {
        var frames = Frames;
        double ppf = PixelsPerFrame;
        int n = FrameCount;

        context.FillRectangle(RulerBrush, new Rect(0, 0, Math.Max(Bounds.Width, TrackWidth), RulerHeight));

        if (n <= 0)
        {
            var empty = new FormattedText("No frames - select an animation",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, TextBrush);
            context.DrawText(empty, new Point(8, RulerHeight + (LaneHeight - empty.Height) / 2));
            return;
        }

        // Lattice grid across the full workspace width (including unused space):
        // minor lines on frame boundaries, major lines on labeled intervals.
        // Same mapping as cells/ruler/playhead, so everything stays aligned
        // under zoom and horizontal scroll.
        double fullWidth = Math.Max(Bounds.Width, TrackWidth);
        var (minorLines, majorLines) = TimelineMath.GridLines(n, ppf, fullWidth, originX: 4);
        foreach (double gx in minorLines)
            context.FillRectangle(MinorGridBrush, new Rect(gx - 0.5, LaneTop, 1, LaneHeight));
        foreach (double gx in majorLines)
            context.FillRectangle(MajorGridBrush, new Rect(gx - 0.5, LaneTop, 1, LaneHeight));
        // Lane separators across the full width (extensible to future tracks).
        context.FillRectangle(LaneSeparatorBrush, new Rect(0, LaneTop, fullWidth, 1));
        context.FillRectangle(LaneSeparatorBrush, new Rect(0, LaneTop + LaneHeight - 1, fullWidth, 1));

        // Ruler ticks + labels. Label gap accounts for "N · m:ss.mmm" text width
        // so labels never overlap at low zoom.
        int every = TimelineMath.LabelEvery(n, ppf, 88.0);
        for (int i = 0; i < n; i++)
        {
            double x = 4 + TimelineMath.XForFrame(i, ppf);
            bool labeled = i % every == 0;
            double tickTop = labeled ? 8 : 18;
            context.FillRectangle(TickBrush, new Rect(x + ppf / 2 - 0.5, tickTop, 1, RulerHeight - tickTop - 2));
            if (labeled)
            {
                var label = new FormattedText($"{i} · {TimelineMath.FormatTime(i, Fps)}",
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 10, TextBrush);
                context.DrawText(label, new Point(x + 3, 6));
            }
        }

        // Frame cells with thumbnails.
        for (int i = 0; i < n; i++)
        {
            double x = 4 + TimelineMath.XForFrame(i, ppf);
            var cell = new Rect(x + 1, LaneTop + 1, Math.Max(1, ppf - 2), LaneHeight - 2);
            var brush = i == CurrentFrame ? CurrentBrush : i < CurrentFrame ? PlayedBrush : CellBrush;
            context.FillRectangle(brush, cell);

            if (frames is not null && (uint)i < (uint)frames.Count && frames[i] is { } thumb)
            {
                double tw = Math.Min(cell.Width - 4, cell.Height - 4);
                var dest = new Rect(cell.Center.X - tw / 2, cell.Center.Y - tw / 2, tw, tw);
                context.DrawImage(thumb, new Rect(0, 0, thumb.PixelSize.Width, thumb.PixelSize.Height), dest);
            }

            if (i == CurrentFrame)
            {
                context.DrawRectangle(CurrentPen, new Rect(x, LaneTop, ppf, LaneHeight));
            }
            if (i == HitFrame)
            {
                // Centered tick, capped in width so it stays subtle at high zoom
                // while remaining precisely inside its frame cell.
                double tickW = Math.Min(Math.Max(1, ppf - 2), 24);
                double tickX = x + (ppf - tickW) / 2;
                context.FillRectangle(HitBrush, new Rect(tickX, LaneTop + 1, tickW, 3));
            }
        }

        // Playhead.
        if (CurrentFrame >= 0)
        {
            double x = 4 + TimelineMath.XForFrame(CurrentFrame, ppf) + ppf / 2;
            context.DrawLine(PlayheadPen, new Point(x, 2), new Point(x, LaneTop + LaneHeight));
            var cap = new StreamGeometry();
            using (var capCtx = cap.Open())
            {
                capCtx.BeginFigure(new Point(x - 5, 0), true);
                capCtx.LineTo(new Point(x + 5, 0));
                capCtx.LineTo(new Point(x, 7));
                capCtx.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, null, cap);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FrameCount <= 0)
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _scrubbing = true;
            e.Pointer.Capture(this);
            ScrubTo(e.GetPosition(this).X - 4);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scrubbing && FrameCount > 0)
            ScrubTo(e.GetPosition(this).X - 4);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _scrubbing = false;
    }

    private void ScrubTo(double x) =>
        ScrubRequested?.Invoke(TimelineMath.FractionAtX(x, FrameCount, PixelsPerFrame));
}
