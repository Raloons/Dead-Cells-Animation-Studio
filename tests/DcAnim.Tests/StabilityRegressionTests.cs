using DcAnim.Core.Export;
using DcAnim.Core.Model;
using DcAnim.Core.Sequence;

namespace DcAnim.Tests;

/// <summary>
/// Regression tests for defects found by the AS-M4.1 in-app workflow audit.
/// Each test pins a crash class: invalid input reaching the model, unbounded
/// history growth, empty-sequence paths, hostile export inputs.
/// </summary>
public sealed class StabilityRegressionTests
{
    private static SequenceDocument NewDoc()
    {
        var doc = new SequenceDocument();
        doc.New("Stability");
        return doc;
    }

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"asm41-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void RejectsNonFiniteTransforms()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => doc.SetClipTransform(c.Id, Transform.Identity with { Opacity = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => doc.SetClipTransform(c.Id, Transform.Identity with { X = double.PositiveInfinity }));
        var e = doc.AddEffect("e.png", 0, 5);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => doc.SetEffectTransform(e.Id, Transform.Identity with { ScaleX = double.NaN }));
        // Rejected edits must not create history entries: undoing twice removes
        // exactly the two successful adds above.
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
        Assert.Empty(doc.Project.Clips);
    }

    [Fact]
    public void RejectsNonFiniteKeyframes()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => doc.AddKeyframe(c.Id, TransformProperty.X, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => doc.AddKeyframe(c.Id, TransformProperty.X, -1, 1));
        doc.AddKeyframe(c.Id, TransformProperty.X, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetKeyframeValue(c.Id, TransformProperty.X, 0, double.NaN, KeyframeInterpolation.Hold));
        var e = doc.AddEffect("e.png", 0, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.AddEffectKeyframe(e.Id, TransformProperty.Y, 0, double.NaN));
    }

    [Fact]
    public void RejectsInvalidScalars()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddClip("B", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetClipSpeed(c.Id, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetFps(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MoveKeyframe(c.Id, TransformProperty.X, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddEffect("x.png", 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.TrimAudio("nope", -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.TrimAudio("nope", 0, 0));
        Assert.Throws<KeyNotFoundException>(() => doc.SetClipSpeed("missing", 1));
        Assert.Throws<KeyNotFoundException>(() => doc.RemoveKeyframe("missing", TransformProperty.X, 0));
        Assert.Throws<KeyNotFoundException>(() => doc.SetKeyframeValue("missing", TransformProperty.X, 0, 1, KeyframeInterpolation.Hold));
        Assert.Throws<ArgumentException>(() => doc.Rename(""));
    }

    [Fact]
    public void UndoHistoryIsCapped()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        for (int i = 0; i < 115; i++)
            doc.SetClipSpeed(doc.Project.Clips[0].Id, 1.0 + (i % 5) * 0.1);
        int n = 0;
        while (doc.History.CanUndo && n < 1000)
        {
            Assert.True(doc.Undo());
            n++;
        }
        Assert.True(n <= 101, $"history cap respected, undid {n}");
        Assert.False(doc.History.CanUndo);
        Assert.False(doc.Undo());
    }

    [Fact]
    public void CoalescedDragIsASingleUndoStep()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.BeginDrag("Move");
        doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 1 });
        doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 2 });
        doc.EndDrag();
        Assert.True(doc.Undo());
        Assert.Equal(0, doc.Project.Clips[0].BaseTransform.X);
        Assert.True(doc.History.CanUndo, "add-clip entry remains after single drag undo");
        Assert.True(doc.Undo()); // removes the added clip
        Assert.False(doc.History.CanUndo);
        Assert.False(doc.Undo());
    }

    [Fact]
    public void AbortedDragRestoresPreDragState()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        var before = doc.Project.Clips[0].BaseTransform;
        doc.BeginDrag("Move");
        doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 42 });
        doc.AbortDrag();
        Assert.Equal(before, doc.Project.Clips[0].BaseTransform);
    }

    [Fact]
    public void CorruptProjectRejected()
    {
        string path = TempPath("corrupt.dcanim");
        try
        {
            File.WriteAllText(path,
                """{"format":"dcanim","version":2,"name":"Bad","fps":30,"clips":[{"id":"c1","animation":"A","speed":1,"transform":null,"keyframes":[{"property":"X","frame":-3,"value":1,"interpolation":"Hold"}]}],"effects":[],"audio":[]}""");
            var ex = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(path));
            Assert.Contains("keyframe", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicateIdsRejected()
    {
        var project = new SequenceProject { Name = "Dup", Fps = 30 };
        project.Clips.Add(new SequenceClip { Id = "same", AnimationId = "A", Speed = 1 });
        project.Clips.Add(new SequenceClip { Id = "same", AnimationId = "B", Speed = 1 });
        string path = TempPath("dup.dcanim");
        try
        {
            // Save succeeds only for valid projects; craft the file directly instead.
            File.WriteAllText(path,
                """{"format":"dcanim","version":2,"name":"Dup","fps":30,"clips":[{"id":"same","animation":"A","speed":1},{"id":"same","animation":"B","speed":1}],"effects":[],"audio":[]}""");
            var ex = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(path));
            Assert.Contains("duplicate", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EngineRejectsEmptyAndOutOfRange()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine();
        Assert.Throws<InvalidOperationException>(() => engine.Play());
        Assert.Throws<InvalidOperationException>(() => engine.Restart());
        Assert.Throws<InvalidOperationException>(() => engine.StepNext());
        Assert.Throws<InvalidOperationException>(() => engine.StepPrevious());
        Assert.Throws<InvalidOperationException>(() => engine.SeekFrame(0));
        Assert.Throws<InvalidOperationException>(() => engine.Scrub(0.5));
        Assert.Throws<ArgumentException>(() => engine.Load(new AnimationClip
        {
            Id = "x", Frames = new List<ClipFrame>(), SourceDescription = "t", DefaultFps = 30,
        }));
        Assert.Equal(-1, engine.CurrentFrameIndex);
        Assert.Null(engine.CurrentFrame);
        engine.Tick(TimeSpan.FromSeconds(1)); // no-op, must not throw
        engine.Pause(); // no-op, must not throw
    }

    [Fact]
    public void ExportersRejectEmptySequence()
    {
        var empty = new RenderedSequence(0, 0, Array.Empty<byte[]>());
        Assert.Throws<InvalidDataException>(() =>
            PngSequenceExporter.ExportRendered(empty, TempPath("e"), true));
        Assert.Throws<InvalidDataException>(() =>
            GifExporter.ExportRendered(empty, 30, TempPath("e.gif"), true));
        Assert.Throws<InvalidDataException>(() =>
            SpriteSheetExporter.Export(empty, Array.Empty<ResolvedClip>(), 30,
                TempPath("s.png"), TempPath("s.json"), 4, 0));
    }

    [Fact]
    public void ExportersHonorCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var frames = new List<byte[]> { new byte[8 * 8 * 4], new byte[8 * 8 * 4] };
        var rs = new RenderedSequence(8, 8, frames);
        var resolved = new[] { new ResolvedClip(
            new SequenceClip { Id = "c", AnimationId = "A", Speed = 1 }, 0, 0, 2, 2) };
        Assert.Throws<OperationCanceledException>(() =>
            PngSequenceExporter.ExportRendered(rs, TempPath("e"), overwrite: true, progress: null, cancellationToken: cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            GifExporter.ExportRendered(rs, 30, TempPath("e.gif"), loop: true, overwrite: true, progress: null, cancellationToken: cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            SpriteSheetExporter.Export(rs, resolved, 30, TempPath("s.png"), TempPath("s.json"), 1, 0, true, null, cts.Token));
    }

    [Fact]
    public void CompositionRejectsOversizeCanvas()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"asm41-big-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string img = Path.Combine(dir, "wide.png");
            using (var bmp = new SkiaSharp.SKBitmap(5000, 100, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul))
            using (var img2 = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var d = img2.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(img, d.ToArray());
            var project = new SequenceProject { Name = "Big", Fps = 30 };
            project.Effects.Add(new EffectClip { Id = "e", ImagePath = img, StartFrame = 0, DurationFrames = 2 });
            using var cache = new EffectImageCache();
            var native = new NativeFrameCache();
            Assert.Throws<InvalidDataException>(() =>
                SequenceCompositor.Render(project, null!, native, cache, out _));
        }
        finally { Directory.Delete(dir, true); }
    }
}
