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
/// Multi-lane sequence track: animation clips, one effect lane, one audio lane
/// (with waveform), keyframe diamonds on the selected item, playhead, and the
/// shared frame lattice. Selection is neutral; only the hit concept (viewer
/// filmstrip) keeps functional color — sequence markers are monochrome.
/// </summary>
public sealed class SequenceTrackControl : Control
{
    public sealed record ClipBlock(
        string ClipId,
        string AnimationId,
        int StartFrame,
        int FrameCount,
        IList<Bitmap> Thumbs);

    public sealed record FxBlock(
        string EffectId,
        string Name,
        int StartFrame,
        int FrameCount,
        int Layer);

    public sealed record AudioBlock(
        string AudioId,
        string Name,
        double StartSeconds,
        double DurationSeconds,
        float[] Peaks,
        bool Muted);

    public sealed record KeyMarker(TransformProperty Property, int Frame, bool Selected);

    public static readonly StyledProperty<IList<ClipBlock>?> ClipsProperty =
        AvaloniaProperty.Register<SequenceTrackControl, IList<ClipBlock>?>(nameof(Clips));

    public static readonly StyledProperty<string?> SelectedClipIdProperty =
        AvaloniaProperty.Register<SequenceTrackControl, string?>(nameof(SelectedClipId));

    public static readonly StyledProperty<IList<FxBlock>?> EffectsProperty =
        AvaloniaProperty.Register<SequenceTrackControl, IList<FxBlock>?>(nameof(Effects));

    public static readonly StyledProperty<string?> SelectedEffectIdProperty =
        AvaloniaProperty.Register<SequenceTrackControl, string?>(nameof(SelectedEffectId));

    public static readonly StyledProperty<IList<AudioBlock>?> AudioProperty =
        AvaloniaProperty.Register<SequenceTrackControl, IList<AudioBlock>?>(nameof(Audio));

    public static readonly StyledProperty<string?> SelectedAudioIdProperty =
        AvaloniaProperty.Register<SequenceTrackControl, string?>(nameof(SelectedAudioId));

    public static readonly StyledProperty<IList<KeyMarker>?> KeyframesProperty =
        AvaloniaProperty.Register<SequenceTrackControl, IList<KeyMarker>?>(nameof(Keyframes));

    public static readonly StyledProperty<KeyMarker?> SelectedKeyProperty =
        AvaloniaProperty.Register<SequenceTrackControl, KeyMarker?>(nameof(SelectedKey));

    public static readonly StyledProperty<bool> ShowEffectKeysProperty =
        AvaloniaProperty.Register<SequenceTrackControl, bool>(nameof(ShowEffectKeys), true);

    public static readonly StyledProperty<int> CurrentFrameProperty =
        AvaloniaProperty.Register<SequenceTrackControl, int>(nameof(CurrentFrame), -1);

    public static readonly StyledProperty<double> PixelsPerFrameProperty =
        AvaloniaProperty.Register<SequenceTrackControl, double>(nameof(PixelsPerFrame), 32.0);

    public static readonly StyledProperty<double> FpsProperty =
        AvaloniaProperty.Register<SequenceTrackControl, double>(nameof(Fps), 30.0);

    public const double RulerHeight = 26.0;
    public const double ClipLaneHeight = 56.0;
    public const double FxLaneHeight = 42.0;
    public const double AudioLaneHeight = 46.0;
    public const double LaneGap = 4.0;
    public const double LaneTop = RulerHeight + LaneGap;
    public double FxTop => LaneTop + ClipLaneHeight + LaneGap;
    public double AudioTop => FxTop + FxLaneHeight + LaneGap;
    public double TrackBottom => AudioTop + AudioLaneHeight;
    private const double OriginX = 4.0;

    private static readonly IBrush RulerBrush = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B));
    private static readonly IBrush ClipBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3D));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5)), 1.5);
    private static readonly IBrush FxBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x22, 0x28));
    private static readonly IBrush AudioBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x26));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x96));
    private static readonly IBrush KeyBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5));
    private static readonly IBrush KeySelectedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC));
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
    private string? _dragEffectId;
    private string? _dragAudioId;
    private (TransformProperty Prop, int Frame)? _dragKey;
    private bool _dragKeyIsEffect;
    private bool _pressArmed; // press converted to coalesced drag once threshold crossed
    private bool _dragging;
    private int _dropIndex = -1; // library drop target, -1 = none
    private double _dropX;        // internal-drag indicator position

    static SequenceTrackControl()
    {
        AffectsRender<SequenceTrackControl>(ClipsProperty, SelectedClipIdProperty,
            EffectsProperty, SelectedEffectIdProperty, AudioProperty, SelectedAudioIdProperty,
            KeyframesProperty, SelectedKeyProperty, CurrentFrameProperty,
            PixelsPerFrameProperty, FpsProperty);
    }

    public event Action<string>? ClipSelected;
    public event Action<string, int>? ClipMoved;
    public event Action<string, int>? LibraryDrop;
    public event Action<string>? RemoveRequested;
    public event Action<string>? DuplicateRequested;
    public event Action<string, double>? SpeedRequested;
    public event Action<string>? EffectSelected;
    public event Action<string, int>? EffectMoved;
    public event Action<string>? EffectRemoveRequested;
    public event Action<string>? AudioSelected;
    public event Action<string, double>? AudioMoved;
    public event Action<string>? AudioRemoveRequested;
    public event Action<TransformProperty, int, bool>? KeyframeSelected; // prop, frame, isEffect
    public event Action<TransformProperty, int, int, bool>? KeyframeMoved; // prop, from, to, isEffect
    public event Action? KeyframeDragStarted;
    public event Action? KeyframeDragFinished;
    /// <summary>Raised when an in-progress gesture is lost (e.g. pointer
    /// capture stolen); hosts must abort any open coalesced undo group.</summary>
    public event Action? DragCancelled;

    public IList<ClipBlock>? Clips { get => GetValue(ClipsProperty); set => SetValue(ClipsProperty, value); }
    public string? SelectedClipId { get => GetValue(SelectedClipIdProperty); set => SetValue(SelectedClipIdProperty, value); }
    public IList<FxBlock>? Effects { get => GetValue(EffectsProperty); set => SetValue(EffectsProperty, value); }
    public string? SelectedEffectId { get => GetValue(SelectedEffectIdProperty); set => SetValue(SelectedEffectIdProperty, value); }
    public IList<AudioBlock>? Audio { get => GetValue(AudioProperty); set => SetValue(AudioProperty, value); }
    public string? SelectedAudioId { get => GetValue(SelectedAudioIdProperty); set => SetValue(SelectedAudioIdProperty, value); }
    public IList<KeyMarker>? Keyframes { get => GetValue(KeyframesProperty); set => SetValue(KeyframesProperty, value); }
    public KeyMarker? SelectedKey { get => GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    public bool ShowEffectKeys { get => GetValue(ShowEffectKeysProperty); set => SetValue(ShowEffectKeysProperty, value); }

    public int CurrentFrame { get => GetValue(CurrentFrameProperty); set => SetValue(CurrentFrameProperty, value); }
    public double PixelsPerFrame { get => GetValue(PixelsPerFrameProperty); set => SetValue(PixelsPerFrameProperty, Math.Clamp(value, 8.0, 128.0)); }
    public double Fps { get => GetValue(FpsProperty); set => SetValue(FpsProperty, value > 0 ? value : 30.0); }

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

    private double FrameX(int frame) => OriginX + TimelineMath.XForFrame(frame, PixelsPerFrame);

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
            return FrameX(last.StartFrame + last.FrameCount);
        }
        return FrameX(clips[insertIndex].StartFrame);
    }

    public override void Render(DrawingContext context)
    {
        var clips = Clips;
        double ppf = PixelsPerFrame;
        int total = TotalFrames;
        double fullWidth = Math.Max(Bounds.Width, TrackWidth);
        context.FillRectangle(RulerBrush, new Rect(0, 0, fullWidth, RulerHeight));

        double laneBottom = AudioTop + AudioLaneHeight;
        if ((clips is null || clips.Count == 0) && total <= 0)
        {
            var empty = new FormattedText("Drop animations here to build a sequence",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, TextBrush);
            context.DrawText(empty, new Point(8, LaneTop + (ClipLaneHeight - empty.Height) / 2));
            return;
        }

        // Lattice across the full workspace.
        var (minors, majors) = TimelineMath.GridLines(total, ppf, fullWidth, OriginX, 88.0);
        foreach (double gx in minors)
            context.FillRectangle(MinorGridBrush, new Rect(gx - 0.5, LaneTop, 1, laneBottom - LaneTop));
        foreach (double gx in majors)
            context.FillRectangle(MajorGridBrush, new Rect(gx - 0.5, LaneTop, 1, laneBottom - LaneTop));
        foreach (double ly in new[] { LaneTop, FxTop, AudioTop, laneBottom })
            context.FillRectangle(LaneSeparatorBrush, new Rect(0, ly, fullWidth, 1));

        // Ruler.
        int every = TimelineMath.LabelEvery(Math.Max(1, total), ppf, 88.0);
        for (int i = 0; i <= total; i++)
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
        if (clips is not null)
        {
            foreach (var c in clips)
            {
                double x = FrameX(c.StartFrame);
                double w = c.FrameCount * ppf;
                bool selected = c.ClipId == SelectedClipId;
                var rect = new Rect(x + 1, LaneTop + 1, Math.Max(1, w - 2), ClipLaneHeight - 2);
                context.FillRectangle(selected ? SelectedBrush : ClipBrush, rect);
                if (selected)
                    context.DrawRectangle(SelectedPen, new Rect(x, LaneTop, w, ClipLaneHeight));
                if (c.Thumbs is { Count: > 0 })
                {
                    var picks = new[] { 0, c.Thumbs.Count / 2, c.Thumbs.Count - 1 }.Distinct().ToList();
                    double slotW = rect.Width / picks.Count;
                    for (int p = 0; p < picks.Count; p++)
                    {
                        var thumb = c.Thumbs[picks[p]];
                        double tw = Math.Min(slotW - 6, rect.Height - 22);
                        if (tw <= 2)
                            continue;
                        var dest = new Rect(rect.X + p * slotW + (slotW - tw) / 2, rect.Center.Y - tw / 2 + 6, tw, tw);
                        context.DrawImage(thumb, new Rect(0, 0, thumb.PixelSize.Width, thumb.PixelSize.Height), dest);
                    }
                }
                DrawTrimmed(context, $"{c.AnimationId} · {c.FrameCount}f", x + 4, LaneTop + 3, Math.Max(0, w - 6));
            }
        }

        // Effect lane.
        if (Effects is { Count: > 0 })
        {
            foreach (var e in Effects)
            {
                double x = FrameX(e.StartFrame);
                double w = e.FrameCount * ppf;
                bool selected = e.EffectId == SelectedEffectId;
                var rect = new Rect(x + 1, FxTop + 1, Math.Max(1, w - 2), FxLaneHeight - 2);
                context.FillRectangle(selected ? SelectedBrush : FxBrush, rect);
                if (selected)
                    context.DrawRectangle(SelectedPen, new Rect(x, FxTop, w, FxLaneHeight));
                DrawTrimmed(context, e.Name, x + 4, FxTop + 3, Math.Max(0, w - 6));
            }
        }

        // Audio lane with waveform.
        if (Audio is { Count: > 0 })
        {
            foreach (var a in Audio)
            {
                int s0 = (int)Math.Round(a.StartSeconds * Fps);
                int n = Math.Max(1, (int)Math.Round(a.DurationSeconds * Fps));
                double x = FrameX(s0);
                double w = n * ppf;
                bool selected = a.AudioId == SelectedAudioId;
                var rect = new Rect(x + 1, AudioTop + 1, Math.Max(1, w - 2), AudioLaneHeight - 2);
                context.FillRectangle(a.Muted ? MutedBrush : AudioBrush, rect);
                if (selected)
                    context.DrawRectangle(SelectedPen, new Rect(x, AudioTop, w, AudioLaneHeight));
                if (a.Peaks.Length > 0 && w > 8)
                {
                    double midY = AudioTop + AudioLaneHeight / 2;
                    double amp = (AudioLaneHeight - 12) / 2;
                    int cols = Math.Max(1, (int)(w - 4));
                    for (int px = 0; px < cols; px++)
                    {
                        int b = px * a.Peaks.Length / cols;
                        float v = a.Peaks[Math.Min(b, a.Peaks.Length - 1)];
                        if (v <= 0.01f)
                            continue;
                        double bh = Math.Max(1, v * amp);
                        context.FillRectangle(WaveBrush, new Rect(x + 2 + px, midY - bh / 2, 1, bh));
                    }
                }
                DrawTrimmed(context, a.Muted ? $"{a.Name} (muted)" : a.Name, x + 4, AudioTop + 3, Math.Max(0, w - 6));
            }
        }

        // Keyframe diamonds on the selected item's lane.
        if (Keyframes is { Count: > 0 })
        {
            bool fx = ShowEffectKeys;
            double anchorStart = fx ? FxAnchorStart() : ClipAnchorStart();
            if (anchorStart >= 0)
            {
                double laneY = fx ? FxTop : LaneTop;
                foreach (var k in Keyframes)
                {
                    double x = FrameX((int)anchorStart + k.Frame);
                    bool sel = SelectedKey is { } s && s.Property == k.Property && s.Frame == k.Frame;
                    var diamond = new StreamGeometry();
                    using (var ctx = diamond.Open())
                    {
                        ctx.BeginFigure(new Point(x, laneY + 8), true);
                        ctx.LineTo(new Point(x + 4, laneY + 12));
                        ctx.LineTo(new Point(x, laneY + 16));
                        ctx.LineTo(new Point(x - 4, laneY + 12));
                        ctx.EndFigure(true);
                    }
                    context.DrawGeometry(sel ? KeySelectedBrush : KeyBrush, null, diamond);
                }
            }
        }

        // Drop-insert indicator.
        if (_dropIndex >= 0 || _dragging)
        {
            double bx = _dropIndex >= 0 ? BoundaryX(_dropIndex) : _dropX;
            context.DrawLine(InsertPen, new Point(bx, 2), new Point(bx, laneBottom));
        }

        // Playhead.
        if (CurrentFrame >= 0 && CurrentFrame < Math.Max(1, total))
        {
            double x = OriginX + TimelineMath.XForFrame(CurrentFrame, ppf) + ppf / 2;
            context.DrawLine(PlayheadPen, new Point(x, 2), new Point(x, laneBottom));
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

    private double ClipAnchorStart()
    {
        var c = Clips?.FirstOrDefault(c => c.ClipId == SelectedClipId);
        return c is null ? -1 : c.StartFrame;
    }

    private double FxAnchorStart()
    {
        var e = Effects?.FirstOrDefault(e => e.EffectId == SelectedEffectId);
        return e is null ? -1 : e.StartFrame;
    }

    private void DrawTrimmed(DrawingContext context, string text, double x, double y, double maxWidth)
    {
        if (maxWidth <= 20)
            return;
        int maxChars = Math.Max(4, (int)(maxWidth / 6));
        string s = text.Length > maxChars ? text[..Math.Max(0, maxChars - 1)] + "…" : text;
        var label = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10, LabelBrush);
        context.DrawText(label, new Point(x, y));
    }

    private enum HitKind { None, Key, Clip, Effect, Audio }

    private (HitKind Kind, string? Id, TransformProperty Prop, int Frame) HitTest(Point pos)
    {
        double x = pos.X, y = pos.Y;
        // Keyframe diamonds first (generous 7px radius).
        if (Keyframes is { Count: > 0 })
        {
            bool fx = ShowEffectKeys;
            double anchor = fx ? FxAnchorStart() : ClipAnchorStart();
            if (anchor >= 0)
            {
                double laneY = (fx ? FxTop : LaneTop) + 12;
                foreach (var k in Keyframes)
                {
                    double kx = FrameX((int)anchor + k.Frame);
                    if (Math.Abs(x - kx) <= 7 && Math.Abs(y - laneY) <= 9)
                        return (HitKind.Key, null, k.Property, k.Frame);
                }
            }
        }
        // Lanes.
        if (y >= AudioTop && Audio is not null)
        {
            foreach (var a in Audio)
            {
                int s0 = (int)Math.Round(a.StartSeconds * Fps);
                int n = Math.Max(1, (int)Math.Round(a.DurationSeconds * Fps));
                if (x >= FrameX(s0) && x < FrameX(s0 + n))
                    return (HitKind.Audio, a.AudioId, default, 0);
            }
            return (HitKind.None, null, default, 0);
        }
        if (y >= FxTop && Effects is not null)
        {
            foreach (var e in Effects)
            {
                if (x >= FrameX(e.StartFrame) && x < FrameX(e.StartFrame + e.FrameCount))
                    return (HitKind.Effect, e.EffectId, default, 0);
            }
            return (HitKind.None, null, default, 0);
        }
        if (y >= LaneTop && Clips is not null)
        {
            foreach (var c in Clips)
            {
                if (x >= FrameX(c.StartFrame) && x < FrameX(c.StartFrame + c.FrameCount))
                    return (HitKind.Clip, c.ClipId, default, 0);
            }
        }
        return (HitKind.None, null, default, 0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_pressPoint is not null)
        {
            // A press arrived while a gesture was still open (e.g. lost
            // release): recycle it as a cancellation so host undo state
            // cannot leak an open coalesce group.
            bool wasActive = _dragging || _pressArmed;
            ResetDragState();
            if (wasActive)
                DragCancelled?.Invoke();
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressPoint = e.GetPosition(this);
        var hit = HitTest(_pressPoint.Value);
        _dragClipId = hit.Kind == HitKind.Clip ? hit.Id : null;
        _dragEffectId = hit.Kind == HitKind.Effect ? hit.Id : null;
        _dragAudioId = hit.Kind == HitKind.Audio ? hit.Id : null;
        _dragKey = hit.Kind == HitKind.Key ? (hit.Prop, hit.Frame) : null;
        _dragKeyIsEffect = ShowEffectKeys;
        _dragging = false;
        _pressArmed = hit.Kind == HitKind.Key;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        bool wasActive = _pressPoint is not null && (_dragging || _pressArmed);
        ResetDragState();
        if (wasActive)
            DragCancelled?.Invoke();
    }

    private void ResetDragState()
    {
        _pressPoint = null;
        _dragClipId = _dragEffectId = _dragAudioId = null;
        _dragKey = null;
        _dragging = false;
        _pressArmed = false;
        _dropIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressPoint is null)
            return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _pressPoint.Value.X;
        var dy = pos.Y - _pressPoint.Value.Y;
        if (!_dragging && dx * dx + dy * dy > 16)
        {
            _dragging = true;
            if (_dragKey is not null && _pressArmed)
            {
                // Begin the coalesced undo group only once real movement starts.
                _pressArmed = false;
                KeyframeDragStarted?.Invoke();
            }
        }
        if (!_dragging)
            return;

        if (_dragKey is { } key)
        {
            // Move keyframe: snap to frame, coalesced by the host.
            int to = (int)Math.Round((pos.X - OriginX) / PixelsPerFrame) -
                (int)(_dragKeyIsEffect ? FxAnchorStart() : ClipAnchorStart());
            KeyframeMoved?.Invoke(key.Prop, key.Frame, Math.Max(0, to), _dragKeyIsEffect);
            _dragKey = (key.Prop, Math.Max(0, to));
            return;
        }
        if (_dragClipId is not null)
        {
            _dropIndex = InsertIndexAt(pos.X);
            _dropX = BoundaryX(_dropIndex);
            InvalidateVisual();
            return;
        }
        if (_dragEffectId is not null || _dragAudioId is not null)
        {
            _dropIndex = -1;
            _dropX = pos.X;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is null)
            return;
        var pos = e.GetPosition(this);
        if (_dragging)
        {
            if (_dragKey is not null)
            {
                KeyframeDragFinished?.Invoke();
            }
            else if (_dragClipId is not null && _dropIndex >= 0)
            {
                var clips = Clips?.ToList() ?? new List<ClipBlock>();
                int from = clips.FindIndex(c => c.ClipId == _dragClipId);
                int to = _dropIndex;
                if (from >= 0 && to > from)
                    to--;
                if (from != to && from >= 0)
                    ClipMoved?.Invoke(_dragClipId, to);
            }
            else if (_dragEffectId is not null)
            {
                int start = Math.Max(0, (int)Math.Round((pos.X - OriginX) / PixelsPerFrame));
                EffectMoved?.Invoke(_dragEffectId, start);
            }
            else if (_dragAudioId is not null)
            {
                int start = Math.Max(0, (int)Math.Round((pos.X - OriginX) / PixelsPerFrame));
                AudioMoved?.Invoke(_dragAudioId, start / Fps);
            }
        }
        else
        {
            var hit = HitTest(pos);
            switch (hit.Kind)
            {
                case HitKind.Clip when hit.Id is { } id: ClipSelected?.Invoke(id); break;
                case HitKind.Effect when hit.Id is { } id: EffectSelected?.Invoke(id); break;
                case HitKind.Audio when hit.Id is { } id: AudioSelected?.Invoke(id); break;
                case HitKind.Key: KeyframeSelected?.Invoke(hit.Prop, hit.Frame, ShowEffectKeys); break;
            }
        }
        _pressArmed = false;
        ResetDragState();
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
        var removeFx = new MenuItem { Header = "Remove effect" };
        removeFx.Click += (_, _) => { if (SelectedEffectId is { } id) EffectRemoveRequested?.Invoke(id); };
        var removeAu = new MenuItem { Header = "Remove audio" };
        removeAu.Click += (_, _) => { if (SelectedAudioId is { } id) AudioRemoveRequested?.Invoke(id); };
        menu.Items.Add(duplicate);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(speedMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeFx);
        menu.Items.Add(removeAu);
        ContextMenu = menu;
    }
}
