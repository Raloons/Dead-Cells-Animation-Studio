namespace DcAnim.Core.Playback;

/// <summary>
/// Pure view-math for the timeline: mapping between frame indexes, pixel
/// positions and time labels. No UI dependencies; fully unit-tested.
/// </summary>
public static class TimelineMath
{
    /// <summary>Width in pixels of one frame cell at the given zoom.</summary>
    public static double PixelsPerFrame(double zoom) => Math.Max(2.0, 16.0 * zoom);

    public static double TrackWidth(int frameCount, double pixelsPerFrame) =>
        Math.Max(0, frameCount * pixelsPerFrame);

    /// <summary>Frame index under horizontal pixel <paramref name="x"/>.</summary>
    public static int FrameAtX(double x, int frameCount, double pixelsPerFrame)
    {
        if (frameCount <= 0 || pixelsPerFrame <= 0)
            return -1;
        int frame = (int)Math.Floor(x / pixelsPerFrame);
        return Math.Clamp(frame, 0, frameCount - 1);
    }

    /// <summary>Scrub fraction in [0,1] for pixel <paramref name="x"/>.</summary>
    public static double FractionAtX(double x, int frameCount, double pixelsPerFrame)
    {
        double width = TrackWidth(frameCount, pixelsPerFrame);
        if (width <= 0)
            return 0;
        return Math.Clamp(x / width, 0.0, 1.0);
    }

    /// <summary>Left-edge pixel of a frame cell.</summary>
    public static double XForFrame(int frame, double pixelsPerFrame) =>
        Math.Max(0, frame) * pixelsPerFrame;

    /// <summary>Ruler label interval (every Nth frame) so labels stay readable.</summary>
    public static int LabelEvery(int frameCount, double pixelsPerFrame, double minLabelGapPx = 48.0)
    {
        if (frameCount <= 0 || pixelsPerFrame <= 0)
            return 1;
        return Math.Max(1, (int)Math.Ceiling(minLabelGapPx / pixelsPerFrame));
    }

    /// <summary>Formats a frame position as time <c>m:ss.mmm</c> at the given FPS.</summary>
    public static string FormatTime(int frame, double fps)
    {
        if (fps <= 0)
            fps = 30.0;
        var t = TimeSpan.FromSeconds(frame / fps);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
