using DcAnim.Core.Model;
using DcAnim.Core.Sources;

namespace DcAnim.Core.Sequence;

/// <summary>
/// One resolved output segment: which clip, where it starts, and how its
/// output frames map to source frames.
/// </summary>
public sealed record ResolvedClip(
    SequenceClip Clip,
    int ClipIndex,
    int StartFrame,
    int OutputFrames,
    int SourceFrames);

/// <summary>
/// Builds the playable/exportable form of a project: an
/// <see cref="AnimationClip"/> whose frames are ordered source-frame
/// references at the project FPS, so preview, scrubbing and all exporters
/// share one timing model. Missing animations are skipped and reported —
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
            int outputs = SequenceTiming.ClipOutputFrames(sources, clip.Speed, project.Fps);
            resolved.Add(new ResolvedClip(clip, i, start, outputs, sources));
            start += outputs;
        }
        return resolved;
    }

    public static int TotalFrames(SequenceProject project, Func<string, int> sourceFrameCount) =>
        Resolve(project, sourceFrameCount).Sum(r => r.OutputFrames);

    /// <summary>
    /// Composes the full ordered frame list. Frames are shared references to
    /// the loader's clip frames (no pixel copies); durations are uniform at
    /// the project FPS. The canvas is the max frame extent, frames centered —
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
                int src = SequenceTiming.SourceIndex(k, r.SourceFrames, r.OutputFrames);
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
