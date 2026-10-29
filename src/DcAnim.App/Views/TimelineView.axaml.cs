using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using DcAnim.App.Controls;
using DcAnim.Core.Playback;
using DcAnim.Core.Sequence;

namespace DcAnim.App.Views;

/// <summary>
/// Bottom editor-style timeline: toolbar (position readout, zoom, fit) plus a
/// scrollable track. Viewer mode shows the single-animation filmstrip;
/// sequence mode shows the multi-clip lane. Both share the ruler, lattice,
/// playhead and zoom model.
/// </summary>
public sealed partial class TimelineView : UserControl
{
    public event Action<double>? ScrubRequested;
    public event Action<string>? SeqClipSelected;
    public event Action<string, int>? SeqClipMoved;
    public event Action<string, int>? SeqLibraryDrop;
    public event Action<string>? SeqRemoveRequested;
    public event Action<string>? SeqDuplicateRequested;
    public event Action<string, double>? SeqSpeedRequested;
    public event Action<string>? SeqEffectSelected;
    public event Action<string, int>? SeqEffectMoved;
    public event Action<string>? SeqEffectRemoveRequested;
    public event Action<string>? SeqAudioSelected;
    public event Action<string, double>? SeqAudioMoved;
    public event Action<string>? SeqAudioRemoveRequested;
    public event Action<TransformProperty, int, bool>? SeqKeyframeSelected;
    public event Action<TransformProperty, int, int, bool>? SeqKeyframeMoved;
    public event Action? SeqKeyframeDragStarted;
    public event Action? SeqKeyframeDragFinished;

    public bool IsSequenceMode { get; private set; }

    public TimelineView()
    {
        InitializeComponent();
        Track.ScrubRequested += f => ScrubRequested?.Invoke(f);
        SeqTrack.ClipSelected += id => SeqClipSelected?.Invoke(id);
        SeqTrack.ClipMoved += (id, i) => SeqClipMoved?.Invoke(id, i);
        SeqTrack.LibraryDrop += (id, i) => SeqLibraryDrop?.Invoke(id, i);
        SeqTrack.RemoveRequested += id => SeqRemoveRequested?.Invoke(id);
        SeqTrack.DuplicateRequested += id => SeqDuplicateRequested?.Invoke(id);
        SeqTrack.SpeedRequested += (id, s) => SeqSpeedRequested?.Invoke(id, s);
        SeqTrack.EffectSelected += id => SeqEffectSelected?.Invoke(id);
        SeqTrack.EffectMoved += (id, f) => SeqEffectMoved?.Invoke(id, f);
        SeqTrack.EffectRemoveRequested += id => SeqEffectRemoveRequested?.Invoke(id);
        SeqTrack.AudioSelected += id => SeqAudioSelected?.Invoke(id);
        SeqTrack.AudioMoved += (id, t) => SeqAudioMoved?.Invoke(id, t);
        SeqTrack.AudioRemoveRequested += id => SeqAudioRemoveRequested?.Invoke(id);
        SeqTrack.KeyframeSelected += (p, f, fx) => SeqKeyframeSelected?.Invoke(p, f, fx);
        SeqTrack.KeyframeMoved += (p, a, b, fx) => SeqKeyframeMoved?.Invoke(p, a, b, fx);
        SeqTrack.KeyframeDragStarted += () => SeqKeyframeDragStarted?.Invoke();
        SeqTrack.KeyframeDragFinished += () => SeqKeyframeDragFinished?.Invoke();
        ZoomSlider.ValueChanged += (_, _) =>
        {
            if (!_settingZoom)
                _timelineUserZoomed = true;
            ApplyZoom();
        };
        FitButton.Click += (_, _) => FitToView();
        TrackScroll.SizeChanged += (_, _) => AutoFitOnLayout();
        TrackScroll.PointerWheelChanged += OnWheel;
        ApplyZoom();
    }

    public void SetSequenceMode(bool sequence)
    {
        IsSequenceMode = sequence;
        Track.IsVisible = !sequence;
        SeqTrack.IsVisible = sequence;
        LayoutTrack();
    }

    private bool _timelineUserZoomed;
    private bool _settingZoom;

    private void SetZoomValue(double zoom)
    {
        _settingZoom = true;
        try
        {
            ZoomSlider.Value = Math.Clamp(zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
        finally
        {
            _settingZoom = false;
        }
    }

    private int ActiveFrameCount =>
        IsSequenceMode ? SeqTrack.TotalFrames : Track.FrameCount;

    private void AutoFitOnLayout()
    {
        if (_timelineUserZoomed || ActiveFrameCount <= 0 || TrackScroll.Bounds.Width <= 0)
            return;
        FitToView();
    }

    // ---------- viewer mode ----------

    public void SetData(IList<Bitmap>? frames, int hitFrame, double fps)
    {
        Track.Frames = frames;
        Track.HitFrame = hitFrame;
        Track.Fps = fps;
        Track.CurrentFrame = frames is { Count: > 0 } ? 0 : -1;
        _timelineUserZoomed = false;
        LayoutTrack();
        AutoFitOnLayout();
        UpdatePositionText(0, frames?.Count ?? 0);
    }

    public void Clear()
    {
        Track.Frames = null;
        Track.CurrentFrame = -1;
        Track.HitFrame = -1;
        SeqTrack.Clips = null;
        SeqTrack.CurrentFrame = -1;
        LayoutTrack();
        PositionText.Text = "—";
    }

    public void SetPosition(int frameIndex, int frameCount, double fps)
    {
        if (IsSequenceMode)
        {
            SeqTrack.CurrentFrame = frameIndex;
            SeqTrack.Fps = fps;
        }
        else
        {
            Track.CurrentFrame = frameIndex;
            Track.Fps = fps;
        }
        UpdatePositionText(frameIndex, frameCount);
        EnsureVisible(frameIndex);
    }

    public void SetFps(double fps)
    {
        Track.Fps = fps;
        SeqTrack.Fps = fps;
    }

    // ---------- sequence mode ----------

    public void SetSequenceData(
        IList<SequenceTrackControl.ClipBlock>? blocks,
        IList<SequenceTrackControl.FxBlock>? effects,
        IList<SequenceTrackControl.AudioBlock>? audio,
        string? selectedClip,
        string? selectedEffect,
        string? selectedAudio,
        IList<SequenceTrackControl.KeyMarker>? keys,
        SequenceTrackControl.KeyMarker? selectedKey,
        bool showEffectKeys,
        int currentFrame,
        double fps)
    {
        SeqTrack.Clips = blocks;
        SeqTrack.Effects = effects;
        SeqTrack.Audio = audio;
        SeqTrack.SelectedClipId = selectedClip;
        SeqTrack.SelectedEffectId = selectedEffect;
        SeqTrack.SelectedAudioId = selectedAudio;
        SeqTrack.Keyframes = keys;
        SeqTrack.SelectedKey = selectedKey;
        SeqTrack.ShowEffectKeys = showEffectKeys;
        SeqTrack.CurrentFrame = currentFrame;
        SeqTrack.Fps = fps;
        _timelineUserZoomed = false;
        LayoutTrack();
        AutoFitOnLayout();
    }

    public void SetSequencePosition(int frameIndex, int frameCount) =>
        SetPosition(frameIndex, frameCount, SeqTrack.Fps);

    /// <summary>Evidence/testing hook: direct timeline zoom control.</summary>
    public void SetZoomFactor(double zoom) => SetZoomValue(zoom);

    public void ZoomIn() => SetZoomValue(ZoomSlider.Value + 0.5);

    public void ZoomOut() => SetZoomValue(ZoomSlider.Value - 0.5);

    private void ApplyZoom()
    {
        double ppf = TimelineMath.PixelsPerFrame(ZoomSlider.Value);
        Track.PixelsPerFrame = ppf;
        SeqTrack.PixelsPerFrame = Math.Min(ppf, 128.0);
        LayoutTrack();
    }

    private void LayoutTrack()
    {
        var active = IsSequenceMode ? (Control)SeqTrack : Track;
        double natural = IsSequenceMode ? SeqTrack.TrackWidth : Track.TrackWidth;
        active.Width = Math.Max(natural, TrackScroll.Bounds.Width);
        active.InvalidateVisual();
    }

    private void FitToView()
    {
        int n = ActiveFrameCount;
        if (n <= 0 || TrackScroll.Bounds.Width <= 0)
            return;
        double ppf = (TrackScroll.Bounds.Width - 12) / n;
        double zoom = Math.Clamp(ppf / 16.0, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _timelineUserZoomed = false; // explicit fit resets the baseline
        SetZoomValue(zoom);
    }

    private void EnsureVisible(int frameIndex)
    {
        if (frameIndex < 0 || TrackScroll.Bounds.Width <= 0)
            return;
        double ppf = IsSequenceMode ? SeqTrack.PixelsPerFrame : Track.PixelsPerFrame;
        double x = 4 + TimelineMath.XForFrame(frameIndex, ppf);
        var offset = TrackScroll.Offset;
        if (x < offset.X)
            TrackScroll.Offset = offset.WithX(Math.Max(0, x - ppf));
        else if (x + ppf > offset.X + TrackScroll.Bounds.Width)
            TrackScroll.Offset = offset.WithX(x + ppf * 2 - TrackScroll.Bounds.Width);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl+wheel zooms the timeline; plain wheel scrolls horizontally.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            _timelineUserZoomed = true;
            double step = e.Delta.Y > 0 ? 0.25 : -0.25;
            SetZoomValue(ZoomSlider.Value + step);
            e.Handled = true;
        }
    }

    private void UpdatePositionText(int frameIndex, int frameCount)
    {
        double fps = IsSequenceMode ? SeqTrack.Fps : Track.Fps;
        PositionText.Text = frameCount <= 0
            ? "—"
            : $"Frame {frameIndex + 1} / {frameCount} · {TimelineMath.FormatTime(frameIndex, fps)}";
    }
}
