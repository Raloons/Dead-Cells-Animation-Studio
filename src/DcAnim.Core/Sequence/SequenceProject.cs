namespace DcAnim.Core.Sequence;

/// <summary>
/// One entry in a sequence project: a reference to a native animation plus a
/// playback speed, an optional base transform and optional transform
/// keyframes (clip-local output frames). Duration and start are derived
/// deterministically, never stored.
/// </summary>
public sealed class SequenceClip
{
    /// <summary>Stable identity for selection/editing; serialized.</summary>
    public required string Id { get; set; }

    /// <summary>Native animation ID (e.g. <c>AtkKatanaB2</c>).</summary>
    public required string AnimationId { get; set; }

    /// <summary>Playback speed multiplier (&gt; 0). 1 = native rate.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Static transform applied under keyframes.</summary>
    public Transform BaseTransform { get; set; } = Transform.Identity;

    /// <summary>Ordered by (Property, Frame) for determinism.</summary>
    public List<TransformKeyframe> Keyframes { get; set; } = new();

    public Transform EvaluateAt(int localFrame) =>
        KeyframeEvaluator.EvaluateAll(Keyframes, localFrame, BaseTransform);
}

/// <summary>
/// Visual-effect overlay: an external transparent image placed on the
/// sequence timeline. Referenced by path (relative to the project file when
/// practical) — never embedded.
/// </summary>
public sealed class EffectClip
{
    public required string Id { get; set; }
    public string Name { get; set; } = "Effect";
    public required string ImagePath { get; set; }
    public int StartFrame { get; set; }
    public int DurationFrames { get; set; } = 30;
    public int Layer { get; set; }
    public Transform BaseTransform { get; set; } = Transform.Identity;
    public List<TransformKeyframe> Keyframes { get; set; } = new();

    public Transform EvaluateAt(int localFrame) =>
        KeyframeEvaluator.EvaluateAll(Keyframes, localFrame, BaseTransform);
}

/// <summary>
/// Audio overlay: an external WAV file placed on the sequence timeline.
/// Times are seconds (project FPS maps them to frames for display).
/// Audio is preview-only — never embedded in PNG/GIF/sheet exports.
/// </summary>
public sealed class AudioClip
{
    public required string Id { get; set; }
    public string Name { get; set; } = "Audio";
    public required string AudioPath { get; set; }
    /// <summary>Sequence time where audible playback begins.</summary>
    public double StartSeconds { get; set; }
    /// <summary>Trim into the source file.</summary>
    public double TrimStartSeconds { get; set; }
    /// <summary> Audible length; null = to end of file.</summary>
    public double? DurationSeconds { get; set; }
    /// <summary>0..1 gain.</summary>
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
}

/// <summary>
/// Editable animation-sequence project. References native animations and
/// external effect/audio files by path; never embeds artwork. Serialized as
/// <c>.dcanim</c> JSON (see <see cref="ProjectSerializer"/>).
/// Format history: v1 = clips only (AS-M3); v2 adds transforms, keyframes,
/// effect and audio tracks (AS-M4, backward-compatible load).
/// </summary>
public sealed class SequenceProject
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled";
    public double Fps { get; set; } = 30.0;
    public List<SequenceClip> Clips { get; set; } = new();
    public List<EffectClip> Effects { get; set; } = new();
    public List<AudioClip> Audio { get; set; } = new();
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
}
