using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using DcAnim.Core.Playback;

namespace DcAnim.App.Views;

/// <summary>
/// Bottom editor-style timeline: toolbar (position readout, zoom, fit) plus a
/// scrollable filmstrip track. Single-animation model today; the track is
/// sized to grow into multi-clip lanes later.
/// </summary>
public sealed partial class TimelineView : UserControl
{
    public event Action<double>? ScrubRequested;

    public TimelineView()
    {
        InitializeComponent();
        Track.ScrubRequested += f => ScrubRequested?.Invoke(f);
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

    private void AutoFitOnLayout()
    {
        if (_timelineUserZoomed || Track.FrameCount <= 0 || TrackScroll.Bounds.Width <= 0)
            return;
        FitToView();
    }

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
        LayoutTrack();
        PositionText.Text = "—";
    }

    public void SetPosition(int frameIndex, int frameCount, double fps)
    {
        Track.CurrentFrame = frameIndex;
        Track.Fps = fps;
        UpdatePositionText(frameIndex, frameCount);
        EnsureVisible(frameIndex);
    }

    public void SetFps(double fps) => Track.Fps = fps;

    /// <summary>Evidence/testing hook: direct timeline zoom control.</summary>
    public void SetZoomFactor(double zoom) => SetZoomValue(zoom);

    public void ZoomIn() => SetZoomValue(ZoomSlider.Value + 0.5);

    public void ZoomOut() => SetZoomValue(ZoomSlider.Value - 0.5);

    private void ApplyZoom()
    {
        Track.PixelsPerFrame = TimelineMath.PixelsPerFrame(ZoomSlider.Value);
        LayoutTrack();
    }

    private void LayoutTrack()
    {
        Track.Width = Math.Max(Track.TrackWidth, TrackScroll.Bounds.Width);
        Track.InvalidateVisual();
    }

    private void FitToView()
    {
        int n = Track.FrameCount;
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
        double x = 4 + TimelineMath.XForFrame(frameIndex, Track.PixelsPerFrame);
        var offset = TrackScroll.Offset;
        if (x < offset.X)
            TrackScroll.Offset = offset.WithX(Math.Max(0, x - Track.PixelsPerFrame));
        else if (x + Track.PixelsPerFrame > offset.X + TrackScroll.Bounds.Width)
            TrackScroll.Offset = offset.WithX(x + Track.PixelsPerFrame * 2 - TrackScroll.Bounds.Width);
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
        PositionText.Text = frameCount <= 0
            ? "—"
            : $"Frame {frameIndex + 1} / {frameCount} · {TimelineMath.FormatTime(frameIndex, Track.Fps)}";
    }
}
