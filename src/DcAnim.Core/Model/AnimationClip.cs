namespace DcAnim.Core.Model;

/// <summary>A single decoded animation frame: full-canvas RGBA pixels.</summary>
public sealed class ClipFrame
{
    public required int Index { get; init; }
    public required string RegionName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Row-major RGBA, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public required byte[] Rgba { get; init; }

    /// <summary>Per-frame display duration. Uniform by default; populated from timing data when available.</summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>Optional gameplay timing overlay for an animation (from CDB weapon rows).</summary>
public sealed record AnimationTiming(
    int? HitFrame,
    double? ChargeSeconds,
    double? AnimSpeed,
    string? FxId,
    string? SourceNote);

/// <summary>A decoded animation: ordered frames plus metadata.</summary>
public sealed class AnimationClip
{
    public required string Id { get; init; }
    public required IReadOnlyList<ClipFrame> Frames { get; init; }
    public required string SourceDescription { get; init; }
    public AnimationTiming? Timing { get; init; }

    /// <summary>Default frames-per-second assumption used when no per-frame timing exists. Documented, user-adjustable.</summary>
    public double DefaultFps { get; init; } = 30.0;

    public int FrameCount => Frames.Count;

    public TimeSpan GetDuration(double speed = 1.0)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        double seconds = 0;
        foreach (var f in Frames)
            seconds += f.Duration.TotalSeconds;
        return TimeSpan.FromSeconds(seconds / speed);
    }
}
