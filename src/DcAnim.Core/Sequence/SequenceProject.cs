namespace DcAnim.Core.Sequence;

/// <summary>
/// One entry in a sequence project: a reference to a native animation plus a
/// playback speed. Duration and start are derived deterministically, never
/// stored, so reordering and speed edits cannot desynchronize the timeline.
/// </summary>
public sealed class SequenceClip
{
    /// <summary>Stable identity for selection/editing; serialized.</summary>
    public required string Id { get; set; }

    /// <summary>Native animation ID (e.g. <c>AtkKatanaB2</c>).</summary>
    public required string AnimationId { get; set; }

    /// <summary>Playback speed multiplier (&gt; 0). 1 = native rate.</summary>
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// Editable animation-sequence project. References native animations by ID;
/// never embeds game artwork. Serialized as <c>.dcanim</c> JSON (see
/// <see cref="ProjectSerializer"/>).
/// </summary>
public sealed class SequenceProject
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled";
    public double Fps { get; set; } = 30.0;
    public List<SequenceClip> Clips { get; set; } = new();
}
