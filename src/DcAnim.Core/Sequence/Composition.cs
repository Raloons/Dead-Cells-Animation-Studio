using DcAnim.Core.Export;
using DcAnim.Core.Model;
using DcAnim.Core.Sources;
using SkiaSharp;

namespace DcAnim.Core.Sequence;

/// <summary>Rendered output: full-canvas straight-alpha RGBA frames.</summary>
public sealed record RenderedSequence(int Width, int Height, IReadOnlyList<byte[]> Frames);

/// <summary>Bounded cache for decoded native frames (immutable RGBA).</summary>
public sealed class NativeFrameCache
{
    private readonly Dictionary<(string Anim, int Index), ClipFrame> _map = new();
    private readonly Queue<(string Anim, int Index)> _order = new();
    private readonly object _gate = new();
    public int Capacity { get; }

    public NativeFrameCache(int capacity = 384)
    {
        Capacity = Math.Max(16, capacity);
    }

    public ClipFrame Get(AnimationLoader loader, string animationId, int index, double fpsHint = 30.0)
    {
        var key = (animationId, index);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var hit))
                return hit;
        }
        var frame = loader.LoadFrame(animationId, index, fpsHint);
        lock (_gate)
        {
            if (!_map.ContainsKey(key))
            {
                _map[key] = frame;
                _order.Enqueue(key);
                while (_map.Count > Capacity && _order.TryDequeue(out var old))
                    _map.Remove(old);
            }
            return _map[key];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}

/// <summary>Image cache for external effect assets, keyed by path + mtime.</summary>
public sealed class EffectImageCache : IDisposable
{
    private readonly Dictionary<string, (DateTime Write, SKBitmap Bmp)> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private bool _disposed;
    public int Capacity { get; }

    public EffectImageCache(int capacity = 64)
    {
        Capacity = Math.Max(4, capacity);
    }

    /// <summary>Returns null for missing/undecodable files (caller skips + warns).</summary>
    public SKBitmap? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        var write = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_map.TryGetValue(path, out var hit) && hit.Write == write)
                return hit.Bmp;
        }
        SKBitmap? bmp = SKBitmap.Decode(path);
        if (bmp is null)
            return null;
        lock (_gate)
        {
            if (_map.TryGetValue(path, out var existing))
            {
                if (existing.Write == write)
                {
                    bmp.Dispose();
                    return existing.Bmp;
                }
                existing.Bmp.Dispose();
                _map.Remove(path);
            }
            _map[path] = (write, bmp);
            _order.Enqueue(path);
            while (_map.Count > Capacity && _order.TryDequeue(out var old))
            {
                if (_map.TryGetValue(old, out var e))
                {
                    e.Bmp.Dispose();
                    _map.Remove(old);
                }
            }
            return _map[path].Bmp;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var (_, bmp) in _map.Values)
                bmp.Dispose();
            _map.Clear();
            _order.Clear();
        }
    }
}

/// <summary>
/// Pure audio-timeline math: which clips are audible at a sequence time and
/// with what file offset. Playback itself lives in the app layer; this keeps
/// scheduling unit-testable and backend-independent.
/// </summary>
public static class AudioTimeline
{
    public sealed record AudibleSpan(AudioClip Clip, double FileOffsetSeconds, double LengthSeconds);

    /// <summary>
    /// Spans audible at sequence time <paramref name="t"/> (seconds).
    /// <paramref name="fileLength"/> resolves null durations / EOF clamping.
    /// </summary>
    public static IReadOnlyList<AudibleSpan> ActiveAt(
        IReadOnlyList<AudioClip> clips, double t, Func<string, double?> fileLength)
    {
        var active = new List<AudibleSpan>();
        foreach (var a in clips)
        {
            if (a.Muted || string.IsNullOrWhiteSpace(a.AudioPath))
                continue;
            if (t < a.StartSeconds)
                continue;
            double? len = fileLength(a.AudioPath);
            double audible = a.DurationSeconds ?? (len is double l ? Math.Max(0, l - a.TrimStartSeconds) : double.PositiveInfinity);
            double local = t - a.StartSeconds;
            if (local < 0 || local >= audible)
                continue;
            if (len is double fl && a.TrimStartSeconds + local >= fl)
                continue; // past EOF
            active.Add(new AudibleSpan(a, a.TrimStartSeconds + local, audible - local));
        }
        return active;
    }
}

/// <summary>
/// Composes final output frames: native clip frames with evaluated
/// transforms, then effect layers (ordered by Layer, then project order),
/// all with nearest-neighbor sampling and straight-alpha compositing.
/// One renderer feeds preview and every exporter (pixel parity by design).
/// </summary>
public static class SequenceCompositor
{
    public static (int Width, int Height) CanvasSize(
        SequenceProject project,
        AnimationLoader loader,
        EffectImageCache? effects = null)
    {
        if (project.CanvasWidth > 0 && project.CanvasHeight > 0)
            return (project.CanvasWidth, project.CanvasHeight);
        int w = 0, h = 0;
        // Base canvas from native frames (first frame is representative;
        // hero frames share canvas sizes in practice).
        foreach (var clip in project.Clips)
        {
            try
            {
                var f = loader.LoadFrame(clip.AnimationId, 0, project.Fps);
                w = Math.Max(w, f.Width);
                h = Math.Max(h, f.Height);
            }
            catch (KeyNotFoundException) { }
        }
        if (effects is not null)
        {
            foreach (var e in project.Effects)
            {
                var bmp = effects.Get(e.ImagePath);
                if (bmp is null)
                    continue;
                w = Math.Max(w, (int)Math.Ceiling(bmp.Width * Math.Abs(e.BaseTransform.ScaleX)));
                h = Math.Max(h, (int)Math.Ceiling(bmp.Height * Math.Abs(e.BaseTransform.ScaleY)));
            }
        }
        if (w <= 0 || h <= 0)
            throw new InvalidDataException("Cannot determine sequence canvas: no resolvable frames.");
        return (w, h);
    }

    /// <summary>Maximum canvas dimension in pixels; larger requests are refused
    /// with a clear error instead of allocating gigabytes.</summary>
    public const int MaxCanvasDimension = 4096;

    public static RenderedSequence Render(
        SequenceProject project,
        AnimationLoader loader,
        NativeFrameCache native,
        EffectImageCache effects,
        out IReadOnlyList<ResolvedClip> resolved,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        resolved = SequenceComposer.Resolve(project, id => loader.Catalog.GetFrameRegions(id).Count);
        var (w, h) = CanvasSize(project, loader, effects);
        if (w > MaxCanvasDimension || h > MaxCanvasDimension)
            throw new InvalidDataException(
                $"Sequence canvas {w}x{h} exceeds the {MaxCanvasDimension}px limit. " +
                "Reduce the canvas size or effect/clip scale.");
        int total = resolved.Sum(r => r.OutputFrames);
        var frames = new List<byte[]>(total);
        int g = 0;
        foreach (var r in resolved)
        {
            for (int k = 0; k < r.OutputFrames; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int src = r.Map.SourceAt(k);
                var native1 = native.Get(loader, r.Clip.AnimationId, src, project.Fps);
                var transform = r.Clip.EvaluateAt(k);
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                DrawLayer(canvas, native1.Rgba, native1.Width, native1.Height, transform, w, h);

                // Effect overlays in layer order.
                foreach (var e in project.Effects.OrderBy(e => e.Layer).ThenBy(e => project.Effects.IndexOf(e)))
                {
                    int local = g - e.StartFrame;
                    if (local < 0 || local >= e.DurationFrames)
                        continue;
                    var bmp = effects.Get(e.ImagePath);
                    if (bmp is null)
                        continue;
                    var et = e.EvaluateAt(local);
                    DrawImage(canvas, bmp, et, w, h);
                }

                using var img = surface.Snapshot();
                using var pix = img.PeekPixels();
                var rgba = new byte[w * h * 4];
                System.Runtime.InteropServices.Marshal.Copy(pix.GetPixels(), rgba, 0, rgba.Length);
                frames.Add(rgba);
                g++;
                progress?.Report(new ExportProgress((double)g / Math.Max(1, total), $"Frame {g} / {total}"));
            }
        }
        return new RenderedSequence(w, h, frames);
    }

    private static void DrawLayer(SKCanvas canvas, byte[] rgba, int sw, int sh, Transform t, int canvasW, int canvasH)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap(sw, sh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            bmp.InstallPixels(new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(), sw * 4);
            DrawImage(canvas, bmp, t, canvasW, canvasH);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void DrawImage(SKCanvas canvas, SKBitmap bmp, Transform t, int canvasW, int canvasH)
    {
        float sx = (float)(t.ScaleX * (t.FlipH ? -1 : 1));
        float sy = (float)(t.ScaleY * (t.FlipV ? -1 : 1));
        if (sx == 0 || sy == 0 || t.Opacity <= 0)
            return;
        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.None, // crisp pixel art
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(t.Opacity * 255, 0, 255)),
        };
        canvas.Save();
        // Pivot point lands on (X, Y). Effects default pivot 0.5/0.5 too; callers
        // position via transform (native frames are pre-centered on canvas size).
        float px = (float)(t.PivotX * bmp.Width);
        float py = (float)(t.PivotY * bmp.Height);
        canvas.Translate((float)(canvasW / 2.0 + t.X), (float)(canvasH / 2.0 + t.Y));
        canvas.RotateDegrees((float)t.RotationDegrees);
        canvas.Scale(sx, sy);
        canvas.Translate(-px, -py);
        canvas.DrawBitmap(bmp, 0, 0, paint);
        canvas.Restore();
    }
}
