namespace DcAnim.Core.Sequence;

/// <summary>
/// Per-frame weapon override stored on a sequence clip. Explicit runs and poses
/// — auto-detect only fills them during editing. Renderer uses them verbatim.
/// </summary>
public sealed class WeaponFrameOverride
{
    public int SourceFrame { get; set; }
    public List<PixelRun> Remove { get; set; } = new();
    public List<PixelRun> Foreground { get; set; } = new();
    public WeaponPose Pose { get; set; } = new(0, 0, 0);
}

public sealed class WeaponClipOverride
{
    public bool Enabled { get; set; } = true;
    public string DonorAnimationId { get; set; } = string.Empty;
    public int DonorFrame { get; set; }
    public List<PixelRun> DonorRegion { get; set; } = new();
    public double DonorPivotX { get; set; }
    public double DonorPivotY { get; set; }
    public bool BehindBody { get; set; }
    /// <summary>When true, all opaque body pixels outside Remove occlude the weapon.</summary>
    public bool PreserveBody { get; set; } = true;
    public int BodyMaskGreen { get; set; } = 32;
    public int DonorMaskGreen { get; set; } = 159;
    /// <summary>When true, weapon = any opaque pixel that is not body color (G != BodyColorGreen).</summary>
    public bool NonBodyMode { get; set; }
    public int BodyColorGreen { get; set; } = 96;
    public List<WeaponFrameOverride> Frames { get; set; } = new();

    public WeaponFrameOverride? ForSource(int sourceFrame) =>
        Frames.FirstOrDefault(f => f.SourceFrame == sourceFrame);

    public WeaponFrameOverride EnsureFrame(int sourceFrame)
    {
        var f = ForSource(sourceFrame);
        if (f is null) { f = new WeaponFrameOverride { SourceFrame = sourceFrame }; Frames.Add(f); Frames.Sort((a, b) => a.SourceFrame.CompareTo(b.SourceFrame)); }
        return f;
    }
}
