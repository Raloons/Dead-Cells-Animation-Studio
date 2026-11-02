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
        IList<Bitmap> Thumbs,
        int SourceFirst,
        int SourceCount,
        int SourceTotal,
        bool Reversed,
        int HoldExtra);

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

    /// <summary>
    /// Inclusive frame-range selection (global output frames). -1/-1 = none.
    /// Set by the host; rendered as a highlight across ruler and clip lane.
    /// </summary>
    public static readonly StyledProperty<int> FrameSelStartProperty =
        AvaloniaProperty.Register<SequenceTrackControl, int>(nameof(FrameSelStart), -1);

    public static readonly StyledProperty<int> FrameSelEndProperty =
        AvaloniaProperty.Register<SequenceTrackControl, int>(nameof(FrameSelEnd), -1);

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
    private double _dragGhostX;   // cursor X for the in-progress clip ghost
    private const double TrimHandle = 6.0;
    private (string ClipId, bool IsLeft)? _hoverTrim;
    private string? _trimClipId;
    private bool _trimLeft;
    private int _rangeAnchor = -1; // shift-drag range selection anchor (global frame)
    private bool _rulerPress;

    private static readonly IBrush RangeBrush = new SolidColorBrush(Color.FromArgb(36, 0xE8, 0xE8, 0xEC));

    private static string ClipLabel(ClipBlock c)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(c.AnimationId).Append(" · ").Append(c.FrameCount).Append('f');
        if (c.SourceFirst > 0 || c.SourceCount < c.SourceTotal)
            sb.Append(" · ").Append(c.SourceFirst + 1).Append('–').Append(c.SourceFirst + c.SourceCount);
        if (c.Reversed)
            sb.Append(" · REV");
        if (c.HoldExtra > 0)
            sb.Append(" · +").Append(c.HoldExtra).Append('H');
        return sb.ToString();
    }

    static SequenceTrackControl()
    {
        AffectsRender<SequenceTrackControl>(ClipsProperty, SelectedClipIdProperty,
            EffectsProperty, SelectedEffectIdProperty, AudioProperty, SelectedAudioIdProperty,
            KeyframesProperty, SelectedKeyProperty, CurrentFrameProperty,
            PixelsPerFrameProperty, FpsProperty, FrameSelStartProperty, FrameSelEndProperty);
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
    public event Action<string, bool>? TrimDragStarted; // clipId, isLeftEdge
    public event Action<string, bool, int>? Trimming; // clipId, isLeftEdge, globalFrame
    public event Action? TrimDragFinished;
    public event Action<int, int>? FrameRangeSelected; // start, endExclusive (global)
    public event Action<int>? SeekRequested; // exact frame from ruler click
    public event Action? SplitRequested;
    public event Action? ReverseRequested;
    public event Action<int>? HoldRequested; // tail hold frames, 0 = clear
    public event Action? FrameCopyRequested;
    public event Action? FrameCutRequested;
    public event Action? FramePasteRequested;
    public event Action? FrameDeleteRequested;

    public IList<ClipBlock>? Clips { get => GetValue(ClipsProperty); set => SetValue(ClipsProperty, value); }
    public string? SelectedClipId { get => GetValue(SelectedClipIdProperty); set => SetValue(SelectedClipIdProperty, value); }
    public IList<FxBlock>? Effects { get => GetValue(EffectsProperty); set => SetValue(EffectsProperty, value); }
    public string? SelectedEffectId { get => GetValue(SelectedEffectIdProperty); set => SetValue(SelectedEffectIdProperty, value); }
    public IList<AudioBlock>? Audio { get => GetValue(AudioProperty); set => SetValue(AudioProperty, value); }
    public string? SelectedAudioId { get => GetValue(SelectedAudioIdProperty); set => SetValue(SelectedAudioIdProperty, value); }
    public IList<KeyMarker>? Keyframes { get => GetValue(KeyframesProperty); set => SetValue(KeyframesProperty, value); }
    public KeyMarker? SelectedKey { get => GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    public bool ShowEffectKeys { get => GetValue(ShowEffectKeysProperty); set => SetValue(ShowEffectKeysProperty, value); }
    public int FrameSelStart { get => GetValue(FrameSelStartProperty); set => SetValue(FrameSelStartProperty, value); }
    public int FrameSelEnd { get => GetValue(FrameSelEndProperty); set => SetValue(FrameSelEndProperty, value); }

    public int CurrentFrame { get => GetValue(CurrentFrameProperty); set => SetValue(CurrentFrameProperty, value); }
    public double PixelsPerFrame { get => GetValue(PixelsPerFrameProperty); set => SetValue(PixelsPerFrameProperty, Math.Clamp(value, 8.0, 128.0)); }
    public double Fps { get => GetValue(FpsProperty); set => SetValue(FpsProperty, value > 0 ? value : 30.0); }
    /// <summary>Estimates output frames for a library animation id (speed 1).
    /// Set by the host so external drags can show an append ghost.</summary>
    public Func<string, int>? EstimateLibraryFrames { get; set; }

    public int TotalFrames => Clips?.Sum(c => c.FrameCount) ?? 0;
    public double TrackWidth => Math.Max(TimelineMath.TrackWidth(TotalFrames, PixelsPerFrame) + 8, 0);

    public SequenceTrackControl()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _dropIndex = -1; _externalDragId = null; _externalGhostFrames = 0; InvalidateVisual(); });
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

        // Frame-range selection highlight (ruler + clip lane).
        if (FrameSelStart >= 0 && FrameSelEnd > FrameSelStart)
        {
            double sx = FrameX(FrameSelStart);
            double ex = FrameX(Math.Min(FrameSelEnd, total));
            context.FillRectangle(RangeBrush, new Rect(sx, 0, Math.Max(1, ex - sx), LaneTop + ClipLaneHeight));
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
                DrawTrimmed(context, ClipLabel(c), x + 4, LaneTop + 3, Math.Max(0, w - 6));
                // Per-frame source indexes at high zoom (1-based, deterministic).
                if (ppf >= 28 && c.FrameCount > 0 && c.FrameCount <= 240 && c.HoldExtra >= 0)
                {
                    try
                    {
                        int visible = Math.Max(1, c.SourceCount);
                        int lead = 0;
                        int baseN = Math.Max(1, c.FrameCount - c.HoldExtra);
                        var map = new SequenceTiming.SpliceMap(c.SourceFirst, visible, c.Reversed, lead, baseN, Math.Max(0, c.HoldExtra));
                        for (int g = 0; g < c.FrameCount; g++)
                        {
                            int src = map.SourceAt(g);
                            var num = new FormattedText((src + 1).ToString(),
                                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                Typeface.Default, 9, TextBrush);
                            double nx = FrameX(c.StartFrame + g) + (ppf - num.Width) / 2;
                            context.DrawText(num, new Point(nx, LaneTop + ClipLaneHeight - num.Height - 3));
                        }
                    }
                    catch (ArgumentOutOfRangeException) { /* transient state: skip labels */ }
                }
                // Trim handles on the selected clip (hover highlight).
                if (selected && w > 24)
                {
                    bool hoverL = _hoverTrim is { IsLeft: true, ClipId: var hl } && hl == c.ClipId;
                    bool hoverR = _hoverTrim is { IsLeft: false, ClipId: var hr } && hr == c.ClipId;
                    context.FillRectangle(hoverL ? KeySelectedBrush : TickBrush,
                        new Rect(x + 1, LaneTop + 1, TrimHandle, ClipLaneHeight - 2));
                    context.FillRectangle(hoverR ? KeySelectedBrush : TickBrush,
                        new Rect(x + w - TrimHandle - 1, LaneTop + 1, TrimHandle, ClipLaneHeight - 2));
                }
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

        // Drop-insert indicator. Thicker and full-height so an append past
        // the last clip is obvious instead of looking like dead space.
        if (_dropIndex >= 0 || _dragging)
        {
            double bx = _dropIndex >= 0 ? BoundaryX(_dropIndex) : _dropX;
            context.DrawLine(InsertPen, new Point(bx, 2), new Point(bx, laneBottom));
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 0xE8, 0xE8, 0xEC)), new Rect(bx - 3, 2, 6, laneBottom - 2));
        }

        // External library drag ghost: estimated append block at the boundary.
        if (_externalDragId is not null && _dropIndex >= 0 && _externalGhostFrames > 0)
        {
            double bx = BoundaryX(_dropIndex);
            double gw = Math.Max(8, _externalGhostFrames * ppf);
            // Clamp inside the visible track; appending past the end still
            // shows the block starting at the end boundary.
            double gx = Math.Clamp(bx, OriginX, Math.Max(OriginX, fullWidth - 8));
            gw = Math.Min(gw, Math.Max(8, fullWidth - gx - 2));
            var ghost = new Rect(gx + 1, LaneTop + 1, Math.Max(7, gw - 2), ClipLaneHeight - 2);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 0xE8, 0xE8, 0xEC)), ghost);
            context.DrawRectangle(InsertPen, ghost);
        }

        // Dragged-clip ghost: follow the cursor so the reorder target is
        // visible instead of only a thin insertion line.
        if (_dragging && _dragClipId is not null && clips is not null)
        {
            var dragged = clips.FirstOrDefault(c => c.ClipId == _dragClipId);
            if (dragged is not null)
            {
                double gw = Math.Max(8, dragged.FrameCount * ppf);
                double gx = Math.Clamp(_dragGhostX - gw / 2, OriginX, Math.Max(OriginX, fullWidth - gw));
                var ghost = new Rect(gx, LaneTop + 1, gw, ClipLaneHeight - 2);
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(90, 0xE8, 0xE8, 0xEC)), ghost);
                context.DrawRectangle(InsertPen, ghost);
                DrawTrimmed(context, ClipLabel(dragged), gx + 4, LaneTop + 3, Math.Max(0, gw - 6));
            }
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

    private enum HitKind { None, Key, Clip, Effect, Audio, TrimLeft, TrimRight, Ruler }

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
        // Trim handles on the selected clip's edges.
        if (y >= LaneTop && y < LaneTop + ClipLaneHeight && Clips is not null)
        {
            foreach (var c in Clips)
            {
                if (c.ClipId != SelectedClipId)
                    continue;
                double w = c.FrameCount * PixelsPerFrame;
                if (w <= 24)
                    break;
                double x0 = FrameX(c.StartFrame);
                if (x >= x0 && x < x0 + TrimHandle)
                    return (HitKind.TrimLeft, c.ClipId, default, 0);
                if (x >= x0 + w - TrimHandle && x < x0 + w)
                    return (HitKind.TrimRight, c.ClipId, default, 0);
            }
        }
        // Ruler (for exact seeking) sits above the lanes.
        if (y >= 0 && y < LaneTop && TotalFrames > 0)
            return (HitKind.Ruler, null, default, 0);
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
        if (y >= LaneTop && y < LaneTop + ClipLaneHeight && Clips is not null)
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
            bool wasActive = _dragging || _pressArmed || _trimClipId is not null || _rangeAnchor >= 0;
            ResetDragState();
            if (wasActive)
                DragCancelled?.Invoke();
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressPoint = e.GetPosition(this);
        var hit = HitTest(_pressPoint.Value);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _dragClipId = hit.Kind == HitKind.Clip ? hit.Id : null;
        _dragEffectId = hit.Kind == HitKind.Effect ? hit.Id : null;
        _dragAudioId = hit.Kind == HitKind.Audio ? hit.Id : null;
        _dragKey = hit.Kind == HitKind.Key ? (hit.Prop, hit.Frame) : null;
        _dragKeyIsEffect = ShowEffectKeys;
        _trimClipId = hit.Kind is HitKind.TrimLeft or HitKind.TrimRight ? hit.Id : null;
        _trimLeft = hit.Kind == HitKind.TrimLeft;
        _rulerPress = hit.Kind == HitKind.Ruler;
        _rangeAnchor = -1;
        if (shift && hit.Kind is HitKind.Clip or HitKind.Ruler or HitKind.Effect or HitKind.Audio)
        {
            // Shift+drag selects an output-frame range; the plain click path
            // below is bypassed so selection never fights seeking.
            _rangeAnchor = FrameAtX(_pressPoint.Value.X);
            _dragClipId = _dragEffectId = _dragAudioId = null;
            _trimClipId = null;
            _rulerPress = false;
        }
        _dragging = false;
        _pressArmed = hit.Kind == HitKind.Key;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private int FrameAtX(double x)
    {
        int total = TotalFrames;
        if (total <= 0)
            return 0;
        return Math.Clamp((int)Math.Floor((x - OriginX) / PixelsPerFrame), 0, total - 1);
    }

    /// <summary>Unclamped output-frame under cursor for trim extension.</summary>
    private int RawFrameAtX(double x) => (int)Math.Floor((x - OriginX) / PixelsPerFrame);

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        bool wasActive = _pressPoint is not null &&
            (_dragging || _pressArmed || _trimClipId is not null || _rangeAnchor >= 0);
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
        _dragGhostX = 0;
        _externalDragId = null;
        _externalGhostFrames = 0;
        _trimClipId = null;
        _rangeAnchor = -1;
        _rulerPress = false;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressPoint is null)
        {
            // Hover feedback for trim handles.
            var hov = HitTest(e.GetPosition(this));
            var want = hov.Kind is HitKind.TrimLeft or HitKind.TrimRight
                ? (hov.Id!, hov.Kind == HitKind.TrimLeft) : ((string, bool)?)null;
            if (want != _hoverTrim)
            {
                _hoverTrim = want;
                Cursor = want is null ? null : new Cursor(StandardCursorType.SizeWestEast);
                InvalidateVisual();
            }
            return;
        }
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
            if (_trimClipId is not null)
                TrimDragStarted?.Invoke(_trimClipId, _trimLeft);
        }
        if (!_dragging)
            return;

        if (_rangeAnchor >= 0)
        {
            // Range selection: anchor frame to current frame under the cursor.
            int cur = FrameAtX(pos.X);
            int a = Math.Min(_rangeAnchor, cur);
            int b = Math.Max(_rangeAnchor, cur) + 1;
            FrameRangeSelected?.Invoke(a, Math.Min(b, Math.Max(1, TotalFrames)));
            return;
        }
        if (_rulerPress)
        {
            // Ruler scrub: drag the playhead continuously. Click-without-drag
            // still seeks once on release; live scrub makes the arrow follow.
            SeekRequested?.Invoke(FrameAtX(pos.X));
            return;
        }
        if (_trimClipId is not null)
        {
            // Unclamped so extending past the current start/end is possible.
            // Shortening stays precise via the anchor map; extension is
            // extrapolated in the host up to the full source range.
            Trimming?.Invoke(_trimClipId, _trimLeft, RawFrameAtX(pos.X));
            return;
        }
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
            _dragGhostX = pos.X;
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
            if (_rangeAnchor >= 0)
            {
                int cur = FrameAtX(pos.X);
                int a = Math.Min(_rangeAnchor, cur);
                int b = Math.Max(_rangeAnchor, cur) + 1;
                FrameRangeSelected?.Invoke(a, Math.Min(b, Math.Max(1, TotalFrames)));
            }
            else if (_trimClipId is not null)
            {
                TrimDragFinished?.Invoke();
            }
            else if (_dragKey is not null)
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
            if (_rangeAnchor >= 0)
            {
                // Shift+click without drag: single-frame selection.
                int f = FrameAtX(pos.X);
                FrameRangeSelected?.Invoke(f, Math.Min(f + 1, Math.Max(1, TotalFrames)));
            }
            else if (_rulerPress)
            {
                SeekRequested?.Invoke(FrameAtX(pos.X));
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
                    case HitKind.Ruler: SeekRequested?.Invoke(FrameAtX(pos.X)); break;
                    case HitKind.TrimLeft when hit.Id is { } tl: ClipSelected?.Invoke(tl); break;
                    case HitKind.TrimRight when hit.Id is { } tr: ClipSelected?.Invoke(tr); break;
                }
            }
        }
        _pressArmed = false;
        ResetDragState();
    }

    private string? _externalDragId;
    private int _externalGhostFrames;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(AssetDnD.Format))
        {
            e.DragEffects = DragDropEffects.Copy;
            _dropIndex = InsertIndexAt(e.GetPosition(this).X);
            _externalDragId = e.Data.Get(AssetDnD.Format) as string;
            _externalGhostFrames = 0;
            if (!string.IsNullOrWhiteSpace(_externalDragId) && EstimateLibraryFrames is { } estimate)
            {
                try { _externalGhostFrames = Math.Max(1, estimate(_externalDragId)); }
                catch { _externalGhostFrames = 0; }
            }
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
        _externalDragId = null;
        _externalGhostFrames = 0;
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
        var splitItem = new MenuItem { Header = "Split clip at playhead  (Ctrl+K)" };
        splitItem.Click += (_, _) => SplitRequested?.Invoke();
        var reverseItem = new MenuItem { Header = "Reverse clip" };
        reverseItem.Click += (_, _) => ReverseRequested?.Invoke();
        var holdMenu = new MenuItem { Header = "Hold last frame…" };
        foreach (int h in new[] { 2, 6, 12, 30 })
        {
            int hold = h;
            var hi = new MenuItem { Header = $"+{hold} frames" };
            hi.Click += (_, _) => HoldRequested?.Invoke(hold);
            holdMenu.Items.Add(hi);
        }
        var clearHold = new MenuItem { Header = "Clear hold" };
        clearHold.Click += (_, _) => HoldRequested?.Invoke(0);
        holdMenu.Items.Add(clearHold);
        menu.Items.Add(splitItem);
        menu.Items.Add(reverseItem);
        menu.Items.Add(holdMenu);
        menu.Items.Add(new Separator());
        var copyItem = new MenuItem { Header = "Copy frames  (Ctrl+C)" };
        copyItem.Click += (_, _) => FrameCopyRequested?.Invoke();
        var cutItem = new MenuItem { Header = "Cut frames  (Ctrl+X)" };
        cutItem.Click += (_, _) => FrameCutRequested?.Invoke();
        var pasteItem = new MenuItem { Header = "Paste frames  (Ctrl+V)" };
        pasteItem.Click += (_, _) => FramePasteRequested?.Invoke();
        var deleteItem = new MenuItem { Header = "Delete frames  (Del)" };
        deleteItem.Click += (_, _) => FrameDeleteRequested?.Invoke();
        menu.Items.Add(copyItem);
        menu.Items.Add(cutItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(deleteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(new Separator());
        menu.Items.Add(removeFx);
        menu.Items.Add(removeAu);
        ContextMenu = menu;
    }
}
