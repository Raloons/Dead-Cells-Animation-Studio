using DcAnim.Core.Model;
using DcAnim.Core.Sources;

namespace DcAnim.Core.Sequence;

/// <summary>
/// One resolved output segment: which clip, where it starts, and how its
/// output frames map to source frames (trim + reverse + holds included).
/// </summary>
public sealed record ResolvedClip(
    SequenceClip Clip,
    int ClipIndex,
    int StartFrame,
    int OutputFrames,
    int SourceFrames,
    int SourceFirst,
    bool Reversed,
    int LeadHoldFrames,
    int TailHoldFrames)
{
    /// <summary>Base (resampled) output count without hold repeats.</summary>
    public int BaseOutputs => OutputFrames - LeadHoldFrames - TailHoldFrames;

    /// <summary>Shared output→source mapping for this segment.</summary>
    public SequenceTiming.SpliceMap Map => new(
        SourceFirst, SourceFrames, Reversed, LeadHoldFrames, BaseOutputs, TailHoldFrames);
}

/// <summary>
/// Builds the playable/exportable form of a project: an
/// <see cref="AnimationClip"/> whose frames are ordered source-frame
/// references at the project FPS, so preview, scrubbing and all exporters
/// share one timing model. Missing animations are skipped and reported -
/// never silently dropped without a trace.
/// </summary>
public static class SequenceComposer
{
    public static IReadOnlyList<ResolvedClip> Resolve(
        SequenceProject project,
        Func<string, int> sourceFrameCount)
    {
        var resolved = new List<ResolvedClip>();
        int start = 0;
        for (int i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            int sources;
            try
            {
                sources = sourceFrameCount(clip.AnimationId);
            }
            catch (KeyNotFoundException)
            {
                continue; // unknown animation: reported via ValidateAnimations, skipped here
            }
            if (sources <= 0)
                continue;
            // Clamp the trim into the available frames (game-version drift):
            // first into range, last into [first..end]. Structural validity
            // (first >= 0, last >= first, holds >= 0) is enforced on write.
            int first = Math.Clamp(clip.SourceFirst, 0, sources - 1);
            int last = Math.Clamp(clip.SourceLast ?? (sources - 1), first, sources - 1);
            int visible = last - first + 1;
            if (visible <= 0)
                continue;
            int lead = Math.Max(0, clip.LeadHoldFrames);
            int tail = Math.Max(0, clip.TailHoldFrames);
            int outputs = SequenceTiming.ClipOutputFrames(visible, clip.Speed, project.Fps, lead, tail);
            resolved.Add(new ResolvedClip(clip, i, start, outputs, visible, first, clip.Reversed, lead, tail));
            start += outputs;
        }
        return resolved;
    }

    public static int TotalFrames(SequenceProject project, Func<string, int> sourceFrameCount) =>
        Resolve(project, sourceFrameCount).Sum(r => r.OutputFrames);

    /// <summary>
    /// Composes the full ordered frame list. Frames are shared references to
    /// the loader's clip frames (no pixel copies); durations are uniform at
    /// the project FPS. The canvas is the max frame extent, frames centered -
    /// see <c>docs/AS-M3-REPORT.md</c> for the coordinate rationale.
    /// </summary>
    public static (AnimationClip Clip, IReadOnlyList<ResolvedClip> Resolved) Compose(
        SequenceProject project,
        AnimationLoader loader)
    {
        var resolved = Resolve(project, id => loader.Catalog.GetFrameRegions(id).Count);
        var frames = new List<ClipFrame>();
        foreach (var r in resolved)
        {
            for (int k = 0; k < r.OutputFrames; k++)
            {
                int src = r.Map.SourceAt(k);
                frames.Add(loader.LoadFrame(r.Clip.AnimationId, src, project.Fps));
            }
        }

        var clip = new AnimationClip
        {
            Id = project.Name,
            Frames = frames,
            SourceDescription = $"Sequence '{project.Name}' ({resolved.Count} clips, {frames.Count} frames @ {project.Fps:0.#} FPS)",
            DefaultFps = project.Fps,
        };
        return (clip, resolved);
    }

    /// <summary>Sequence canvas: max extent over all composed frames, origin top-left.</summary>
    public static (int Width, int Height) CanvasSize(IEnumerable<ClipFrame> frames)
    {
        int w = 0, h = 0;
        foreach (var f in frames)
        {
            w = Math.Max(w, f.Width);
            h = Math.Max(h, f.Height);
        }
        return (w, h);
    }
}
