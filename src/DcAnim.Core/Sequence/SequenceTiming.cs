namespace DcAnim.Core.Sequence;

/// <summary>
/// Deterministic sequence timing. Timebase is integer output frames at the
/// project FPS; native source order is preserved, never interpolated.
/// <para/>
/// For a clip with <c>S</c> source frames, speed <c>v</c>, project FPS
/// <c>f</c> and native rate 30 FPS, the clip occupies
/// <c>N = max(1, round(S · f / (30 · v)))</c> output frames. Output frame
/// <c>k</c> shows source frame <c>min(S−1, ⌊k · S / N⌋)</c>: slow speeds hold
/// source frames, fast speeds skip them, and clip boundaries land exactly
/// because every count is an integer (no floating-point drift).
/// </summary>
public static class SequenceTiming
{
    public const double NativeFps = 30.0;

    /// <summary>Output frame count for a clip, given its source frame count.</summary>
    public static int ClipOutputFrames(int sourceFrames, double speed, double projectFps)
    {
        if (sourceFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrames));
        if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (projectFps <= 0 || double.IsNaN(projectFps) || double.IsInfinity(projectFps))
            throw new ArgumentOutOfRangeException(nameof(projectFps));
        return Math.Max(1, (int)Math.Round(sourceFrames * projectFps / (NativeFps * speed), MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Output frame count including freeze holds: resampled base outputs for
    /// the visible sources plus lead/tail repeats (which need no resampling).
    /// </summary>
    public static int ClipOutputFrames(int visibleSources, double speed, double projectFps, int leadHold, int tailHold)
    {
        if (leadHold < 0)
            throw new ArgumentOutOfRangeException(nameof(leadHold));
        if (tailHold < 0)
            throw new ArgumentOutOfRangeException(nameof(tailHold));
        return ClipOutputFrames(visibleSources, speed, projectFps) + leadHold + tailHold;
    }

    /// <summary>
    /// Visible source count after clamping a trim to the available frames.
    /// Never negative: returns 0 when there is nothing to show.
    /// </summary>
    public static int VisibleSourceFrames(int sourceCount, int first, int? last)
    {
        if (sourceCount <= 0)
            return 0;
        int f = Math.Clamp(first, 0, sourceCount - 1);
        int l = Math.Clamp(last ?? (sourceCount - 1), f, sourceCount - 1);
        return l - f + 1;
    }

    /// <summary>Source frame index shown by output frame <paramref name="k"/>.</summary>
    public static int SourceIndex(int k, int sourceFrames, int outputFrames)
    {
        if (k < 0 || k >= outputFrames)
            throw new ArgumentOutOfRangeException(nameof(k));
        if (sourceFrames <= 0 || outputFrames <= 0)
            throw new ArgumentOutOfRangeException("Frame counts must be positive.");
        return Math.Min(sourceFrames - 1, (int)((long)k * sourceFrames / outputFrames));
    }

    /// <summary>
    /// Output→source mapping for one spliced clip, including trim, reverse
    /// and hold zones. The single choke point shared by the renderer,
    /// viewport overlay, timeline labels and keyframe remapping, so preview
    /// and exports agree by construction.
    /// <para/>
    /// Layout of the <see cref="TotalOutputs"/> frames: <see cref="LeadHold"/>
    /// repeats of the first displayed frame, then <see cref="BaseOutputs"/>
    /// resampled frames over the visible source range, then
    /// <see cref="TailHold"/> repeats of the final displayed frame.
    /// </summary>
    public readonly record struct SpliceMap(
        int SourceFirst,
        int VisibleCount,
        bool Reversed,
        int LeadHold,
        int BaseOutputs,
        int TailHold)
    {
        public int TotalOutputs => LeadHold + BaseOutputs + TailHold;

        public int SourceLast => SourceFirst + VisibleCount - 1;

        /// <summary>Source shown by hold zones: first displayed when reversed, else last.</summary>
        public int HoldSource => Reversed ? SourceFirst : SourceLast;

        /// <summary>0-based source frame shown by output frame <paramref name="k"/>.</summary>
        public int SourceAt(int k)
        {
            if (VisibleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(k), "No visible source frames.");
            if (BaseOutputs <= 0)
                throw new ArgumentOutOfRangeException(nameof(k), "No base output frames.");
            if (LeadHold < 0 || TailHold < 0)
                throw new ArgumentOutOfRangeException("Hold counts must not be negative.");
            if (k < 0 || k >= TotalOutputs)
                throw new ArgumentOutOfRangeException(nameof(k));
            int v;
            if (k < LeadHold)
                v = 0;
            else if (k < LeadHold + BaseOutputs)
                v = SourceIndex(k - LeadHold, VisibleCount, BaseOutputs);
            else
                v = VisibleCount - 1;
            return SourceFirst + (Reversed ? VisibleCount - 1 - v : v);
        }
    }

    /// <summary>Start output frame of each clip for a contiguous track.</summary>
    public static int[] ClipStartFrames(IReadOnlyList<int> clipOutputFrames)
    {
        var starts = new int[clipOutputFrames.Count];
        int t = 0;
        for (int i = 0; i < clipOutputFrames.Count; i++)
        {
            starts[i] = t;
            t += clipOutputFrames[i];
        }
        return starts;
    }

    /// <summary>Clip index containing global output frame <paramref name="g"/>.</summary>
    public static int ClipIndexAt(int g, int[] starts, IReadOnlyList<int> counts)
    {
        if (starts.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(g), "Sequence is empty.");
        if (g < 0)
            return 0;
        for (int i = starts.Length - 1; i >= 0; i--)
        {
            if (g >= starts[i])
                return Math.Min(i, counts.Count - 1);
        }
        return 0;
    }

    /// <summary>
    /// Drop/reorder insertion index for a horizontal timeline position: the
    /// first clip whose midpoint lies right of <paramref name="x"/>, else
    /// append. Pure function of clip starts/counts so drop targeting is
    /// unit-testable; the view only converts pixels to frames.
    /// </summary>
    public static int InsertIndexAt(int[] starts, IReadOnlyList<int> counts, double x, double pixelsPerFrame, double originX = 0)
    {
        if (starts.Length == 0)
            return 0;
        double lx = x - originX;
        for (int i = 0; i < starts.Length; i++)
        {
            double midX = (starts[i] + counts[i] / 2.0) * pixelsPerFrame;
            if (lx < midX)
                return i;
        }
        return starts.Length;
    }
}
