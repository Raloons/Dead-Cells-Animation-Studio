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

    /// <summary>
    /// Vertical grid-line positions for the timeline lattice. Minor lines sit
    /// on every frame boundary; major lines sit on labeled intervals
    /// (<see cref="LabelEvery"/>). Lines cover the full <paramref name="width"/>
    /// (including unused space right of the animation) and scroll/zoom with
    /// the track because they derive from the same frame mapping.
    /// </summary>
    public static (double[] Minor, double[] Major) GridLines(
        int frameCount, double pixelsPerFrame, double width, double originX = 0, double minLabelGapPx = 88.0)
    {
        if (frameCount <= 0 || pixelsPerFrame <= 0 || width <= 0)
            return (Array.Empty<double>(), Array.Empty<double>());
        int last = (int)Math.Ceiling((width - originX) / pixelsPerFrame);
        var minor = new List<double>(last + 1);
        var major = new List<double>();
        int every = LabelEvery(frameCount, pixelsPerFrame, minLabelGapPx);
        for (int i = 0; i <= last; i++)
        {
            double x = originX + XForFrame(i, pixelsPerFrame);
            if (x > width)
                break;
            minor.Add(x);
            if (i % every == 0)
                major.Add(x);
        }
        return (minor.ToArray(), major.ToArray());
    }
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

    /// <summary>
    /// Neighbor output frames for onion-skin overlays: up to
    /// <paramref name="count"/> frames on each side of
    /// <paramref name="frame"/>, clamped to <c>[0, totalFrames)</c> (no
    /// wrap). Previous is ordered far-to-near, next near-to-far — paint
    /// order, so nearer ghosts draw on top.
    /// </summary>
    public static (int[] Previous, int[] Next) OnionFrames(int frame, int totalFrames, int count)
    {
        count = Math.Clamp(count, 0, 3);
        var prev = new List<int>();
        var next = new List<int>();
        for (int i = count; i >= 1; i--)
        {
            if (frame - i >= 0)
                prev.Add(frame - i);
        }
        for (int i = 1; i <= count; i++)
        {
            if (frame + i < totalFrames)
                next.Add(frame + i);
        }
        return (prev.ToArray(), next.ToArray());
    }
}
