using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DcAnim.App.Services;
using DcAnim.Core.Playback;
using DcAnim.Core.Sequence;

namespace DcAnim.App.Controls;

/// <summary>
/// Single-track sequence lane: ordered clip blocks with thumbnails, selection,
/// drag-reorder, drop-to-insert from the asset library, and a context menu.
/// Neutral styling; geometry reuses <see cref="TimelineMath"/> so ruler,
/// clips, playhead and the viewer lattice stay aligned.
/// </summary>
public sealed class SequenceTrackControl : Control
{
    public sealed record ClipBlock(
        string ClipId,
        string AnimationId,
        int StartFrame,
        int FrameCount,
        IList<Bitmap>? Thumbs);

    public static readonly StyledProperty<IList<ClipBlock>?> ClipsProperty =
        AvaloniaProperty.Register<SequenceTrackControl, IList<ClipBlock>?>(nameof(Clips));

    public static readonly StyledProperty<string?> SelectedClipIdProperty =
        AvaloniaProperty.Register<SequenceTrackControl, string?>(nameof(SelectedClipId));

    public static readonly StyledProperty<int> CurrentFrameProperty =
        AvaloniaProperty.Register<SequenceTrackControl, int>(nameof(CurrentFrame), -1);

    public static readonly StyledProperty<double> PixelsPerFrameProperty =
        AvaloniaProperty.Register<SequenceTrackControl, double>(nameof(PixelsPerFrame), 32.0);

    public static readonly StyledProperty<double> FpsProperty =
        AvaloniaProperty.Register<SequenceTrackControl, double>(nameof(Fps), 30.0);

    public const double RulerHeight = 26.0;
    public const double LaneHeight = 64.0;
    public const double LaneTop = RulerHeight + 4;
    private const double OriginX = 4.0;

    private static readonly IBrush RulerBrush = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B));
    private static readonly IBrush ClipBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3D));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5)), 1.5);
    private static readonly IBrush MinorGridBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x27));
    private static readonly IBrush MajorGridBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x31, 0x38));
    private static readonly IBrush LaneSeparatorBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x7F));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xA9, 0xB3));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)), 1.5);
    private static readonly IPen InsertPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)), 2);

    private Point? _pressPoint;
    private string? _dragClipId;
    private bool _dragging;
    private int _dropIndex = -1; // external library drop target, -1 = none

    static SequenceTrackControl()
    {
        AffectsRender<SequenceTrackControl>(ClipsProperty, SelectedClipIdProperty,
            CurrentFrameProperty, PixelsPerFrameProperty, FpsProperty);
    }

    public event Action<string>? ClipSelected;
    public event Action<string, int>? ClipMoved; // clip id, new clip index
    public event Action<string, int>? LibraryDrop; // animation id, insert clip index
    public event Action<string>? RemoveRequested;
    public event Action<string>? DuplicateRequested;
    public event Action<string, double>? SpeedRequested;

    public IList<ClipBlock>? Clips
    {
        get => GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }

    public string? SelectedClipId
    {
        get => GetValue(SelectedClipIdProperty);
        set => SetValue(SelectedClipIdProperty, value);
    }

    public int CurrentFrame
    {
        get => GetValue(CurrentFrameProperty);
        set => SetValue(CurrentFrameProperty, value);
    }

    public double PixelsPerFrame
    {
        get => GetValue(PixelsPerFrameProperty);
        set => SetValue(PixelsPerFrameProperty, Math.Clamp(value, 8.0, 128.0));
    }

    public double Fps
    {
        get => GetValue(FpsProperty);
        set => SetValue(FpsProperty, value > 0 ? value : 30.0);
    }

    public int TotalFrames => Clips?.Sum(c => c.FrameCount) ?? 0;
    public double TrackWidth => Math.Max(TimelineMath.TrackWidth(TotalFrames, PixelsPerFrame) + 8, 0);

    public SequenceTrackControl()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _dropIndex = -1; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
        BuildContextMenu();
    }

    public int InsertIndexAt(double x)
    {
        var clips = Clips;
        if (clips is null || clips.Count == 0)
            return 0;
        int[] starts = clips.Select(c => c.StartFrame).ToArray();
        int[] counts = clips.Select(c => c.FrameCount).ToArray();
        return SequenceTiming.InsertIndexAt(starts, counts, x, PixelsPerFrame, OriginX);
    }

    public double BoundaryX(int insertIndex)
    {
        var clips = Clips;
        if (clips is null || clips.Count == 0)
            return OriginX;
        if (insertIndex >= clips.Count)
        {
            var last = clips[^1];
            return OriginX + TimelineMath.XForFrame(last.StartFrame + last.FrameCount, PixelsPerFrame);
        }
        return OriginX + TimelineMath.XForFrame(clips[insertIndex].StartFrame, PixelsPerFrame);
    }

    public override void Render(DrawingContext context)
    {
        var clips = Clips;
        double ppf = PixelsPerFrame;
        double fullWidth = Math.Max(Bounds.Width, TrackWidth);
        context.FillRectangle(RulerBrush, new Rect(0, 0, fullWidth, RulerHeight));

        int total = TotalFrames;
        if (clips is null || clips.Count == 0 || total <= 0)
        {
            var empty = new FormattedText("Drop animations here to build a sequence",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, TextBrush);
            context.DrawText(empty, new Point(8, LaneTop + (LaneHeight - empty.Height) / 2));
            return;
        }

        // Lattice.
        var (minors, majors) = TimelineMath.GridLines(total, ppf, fullWidth, OriginX, 88.0);
        foreach (double gx in minors)
            context.FillRectangle(MinorGridBrush, new Rect(gx - 0.5, LaneTop, 1, LaneHeight));
        foreach (double gx in majors)
            context.FillRectangle(MajorGridBrush, new Rect(gx - 0.5, LaneTop, 1, LaneHeight));
        context.FillRectangle(LaneSeparatorBrush, new Rect(0, LaneTop, fullWidth, 1));
        context.FillRectangle(LaneSeparatorBrush, new Rect(0, LaneTop + LaneHeight - 1, fullWidth, 1));

        // Ruler.
        int every = TimelineMath.LabelEvery(total, ppf, 88.0);
        for (int i = 0; i <= total; i += 1)
        {
            double x = OriginX + TimelineMath.XForFrame(i, ppf);
            if (x > fullWidth)
                break;
            bool labeled = i % every == 0;
            double tickTop = labeled ? 8 : 18;
            context.FillRectangle(TickBrush, new Rect(x - 0.5, tickTop, 1, RulerHeight - tickTop - 2));
            if (labeled && i < total)
            {
                var label = new FormattedText($"{i} · {TimelineMath.FormatTime(i, Fps)}",
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 10, TextBrush);
                context.DrawText(label, new Point(x + 3, 6));
            }
        }

        // Clip blocks.
        foreach (var c in clips)
        {
            double x = OriginX + TimelineMath.XForFrame(c.StartFrame, ppf);
            double w = c.FrameCount * ppf;
            bool selected = c.ClipId == SelectedClipId;
            var rect = new Rect(x + 1, LaneTop + 1, Math.Max(1, w - 2), LaneHeight - 2);
            context.FillRectangle(selected ? SelectedBrush : ClipBrush, rect);
            if (selected)
                context.DrawRectangle(SelectedPen, new Rect(x, LaneTop, w, LaneHeight));

            // Thumbnails: first, middle, last.
            if (c.Thumbs is { Count: > 0 })
            {
                var picks = new[] { 0, c.Thumbs.Count / 2, c.Thumbs.Count - 1 }.Distinct().ToList();
                double slotW = rect.Width / picks.Count;
                for (int p = 0; p < picks.Count; p++)
                {
                    var thumb = c.Thumbs[picks[p]];
                    double tw = Math.Min(slotW - 6, rect.Height - 20);
                    if (tw <= 2)
                        continue;
                    var dest = new Rect(rect.X + p * slotW + (slotW - tw) / 2, rect.Center.Y - tw / 2 + 6, tw, tw);
                    context.DrawImage(thumb, new Rect(0, 0, thumb.PixelSize.Width, thumb.PixelSize.Height), dest);
                }
            }

            var label = $"{c.AnimationId} · {c.FrameCount}f";
            double maxText = Math.Max(0, w - 6);
            if (maxText > 20)
            {
                // Approximate trim to the block width (monospace estimate).
                int maxChars = Math.Max(4, (int)(maxText / 6));
                string s = label.Length > maxChars ? label[..Math.Max(0, maxChars - 1)] + "…" : label;
                var name = new FormattedText(s,
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 10, LabelBrush);
                context.DrawText(name, new Point(x + 4, LaneTop + 3));
            }
        }

        // Drop-insert indicator.
        if (_dropIndex >= 0)
        {
            double bx = BoundaryX(_dropIndex);
            context.DrawLine(InsertPen, new Point(bx, 2), new Point(bx, LaneTop + LaneHeight));
        }

        // Playhead.
        if (CurrentFrame >= 0 && CurrentFrame < total)
        {
            double x = OriginX + TimelineMath.XForFrame(CurrentFrame, ppf) + ppf / 2;
            context.DrawLine(PlayheadPen, new Point(x, 2), new Point(x, LaneTop + LaneHeight));
            var cap = new StreamGeometry();
            using (var ctx = cap.Open())
            {
                ctx.BeginFigure(new Point(x - 5, 0), true);
                ctx.LineTo(new Point(x + 5, 0));
                ctx.LineTo(new Point(x, 7));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, null, cap);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(this);
            _dragClipId = ClipAt(_pressPoint.Value.X);
            _dragging = false;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressPoint is null || _dragClipId is null)
            return;
        var pos = e.GetPosition(this);
        if (!_dragging && _pressPoint is { } start &&
            (pos.X - start.X) * (pos.X - start.X) + (pos.Y - start.Y) * (pos.Y - start.Y) > 16)
            _dragging = true;
        if (_dragging)
        {
            _dropIndex = InsertIndexAt(pos.X);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is null)
            return;
        var pos = e.GetPosition(this);
        if (_dragging && _dragClipId is not null && _dropIndex >= 0)
        {
            // Convert boundary index to clip index accounting for the dragged clip.
            var clips = Clips ?? new List<ClipBlock>();
            int from = clips.ToList().FindIndex(c => c.ClipId == _dragClipId);
            int to = _dropIndex;
            if (from >= 0 && to > from)
                to--;
            if (from != to)
                ClipMoved?.Invoke(_dragClipId, to);
        }
        else if (_dragClipId is not null)
        {
            ClipSelected?.Invoke(_dragClipId);
        }
        _pressPoint = null;
        _dragClipId = null;
        _dragging = false;
        _dropIndex = -1;
        InvalidateVisual();
    }

    private string? ClipAt(double x)
    {
        var clips = Clips;
        if (clips is null)
            return null;
        double lx = x - OriginX;
        foreach (var c in clips)
        {
            double sx = TimelineMath.XForFrame(c.StartFrame, PixelsPerFrame);
            if (lx >= sx && lx < sx + c.FrameCount * PixelsPerFrame)
                return c.ClipId;
        }
        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(AssetDnD.Format))
        {
            e.DragEffects = DragDropEffects.Copy;
            _dropIndex = InsertIndexAt(e.GetPosition(this).X);
            InvalidateVisual();
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        int index = _dropIndex >= 0 ? _dropIndex : InsertIndexAt(e.GetPosition(this).X);
        _dropIndex = -1;
        InvalidateVisual();
        if (e.Data.Get(AssetDnD.Format) is string id && !string.IsNullOrWhiteSpace(id))
            LibraryDrop?.Invoke(id, index);
        e.Handled = true;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        var duplicate = new MenuItem { Header = "Duplicate clip" };
        duplicate.Click += (_, _) => { if (SelectedClipId is { } id) DuplicateRequested?.Invoke(id); };
        var remove = new MenuItem { Header = "Remove clip" };
        remove.Click += (_, _) => { if (SelectedClipId is { } id) RemoveRequested?.Invoke(id); };
        var speedMenu = new MenuItem { Header = "Speed" };
        foreach (double s in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            double speed = s;
            var item = new MenuItem { Header = $"{speed:0.##}×" };
            item.Click += (_, _) => { if (SelectedClipId is { } id) SpeedRequested?.Invoke(id, speed); };
            speedMenu.Items.Add(item);
        }
        menu.Items.Add(duplicate);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(speedMenu);
        ContextMenu = menu;
    }
}
