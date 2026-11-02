namespace DcAnim.Core.Sequence;

/// <summary>
/// One entry in a sequence project: a reference to a native animation plus a
/// playback speed, an optional base transform and optional transform
/// keyframes (clip-local output frames). Duration and start are derived
/// deterministically, never stored.
/// <para/>
/// Frame indexing convention: <see cref="SourceFirst"/> and
/// <see cref="SourceLast"/> are <b>0-based inclusive</b> native source frame
/// indexes. The UI displays them 1-based ("Frames 4–8 of 26"). Timeline and
/// playhead positions are 0-based global output frames. A clip plays the
/// visible source range <c>[SourceFirst..SourceLast]</c> (or to the last
/// available frame when <see cref="SourceLast"/> is null), optionally
/// <see cref="Reversed"/>, with <see cref="LeadHoldFrames"/> repeats of the
/// first displayed frame prepended and <see cref="TailHoldFrames"/> repeats
/// of the final displayed frame appended. Trimming is non-destructive: the
/// native animation is never modified.
/// </summary>
public sealed class SequenceClip
{
    /// <summary>Stable identity for selection/editing; serialized.</summary>
    public required string Id { get; set; }

    /// <summary>Native animation ID (e.g. <c>AtkKatanaB2</c>).</summary>
    public required string AnimationId { get; set; }

    /// <summary>Playback speed multiplier (&gt; 0). 1 = native rate.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>First visible native source frame (0-based, inclusive).</summary>
    public int SourceFirst { get; set; } = 0;

    /// <summary>
    /// Last visible native source frame (0-based, inclusive), or null for
    /// the last available frame. Resolved against the catalog at compose
    /// time, so game-version drift clamps instead of crashing.
    /// </summary>
    public int? SourceLast { get; set; }

    /// <summary>Play the visible range backward. Source data is untouched.</summary>
    public bool Reversed { get; set; }

    /// <summary>
    /// Repeats of the first displayed frame prepended to the clip. User
    /// editing only creates tail holds; a nonzero lead hold is written by
    /// Split to keep seam timing (documented in the AS-M4.2 report).
    /// </summary>
    public int LeadHoldFrames { get; set; }

    /// <summary>
    /// Repeats of the final displayed frame appended to the clip
    /// (freeze-pose hold). Efficient: no pixel data is duplicated.
    /// </summary>
    public int TailHoldFrames { get; set; }

    /// <summary>Maximum hold length (either end) in output frames.</summary>
    public const int MaxHoldFrames = 10000;

    /// <summary>Static transform applied under keyframes.</summary>
    public Transform BaseTransform { get; set; } = Transform.Identity;

    /// <summary>Ordered by (Property, Frame) for determinism.</summary>
    public List<TransformKeyframe> Keyframes { get; set; } = new();

    /// <summary>
    /// Optional weapon substitution: erase the source weapon pixels and compose
    /// a donor weapon in their place. Null = disabled. Explicit runs/poses only;
    /// the renderer never infers colors.
    /// </summary>
    public WeaponClipOverride? Weapon { get; set; }

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
/// effect and audio tracks (AS-M4, backward-compatible load); v3 adds
/// per-clip source trim, reverse and hold frames (AS-M4.2). v4 adds optional
/// per-clip weapon substitution (explicit runs/poses, backward-compatible load).
/// Older versions load with documented defaults (full range, forward, no holds,
/// no weapon override).
/// </summary>
public sealed class SequenceProject
{
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled";
    public double Fps { get; set; } = 30.0;
    public List<SequenceClip> Clips { get; set; } = new();
    public List<EffectClip> Effects { get; set; } = new();
    public List<AudioClip> Audio { get; set; } = new();
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }

    /// <summary>
    /// When true (default), structural edits that change total duration shift
    /// effects/audio at or after the edited point by the same delta, keeping
    /// synchronization. When false they stay at absolute frames.
    /// </summary>
    public bool RippleFxAudio { get; set; } = true;
}
