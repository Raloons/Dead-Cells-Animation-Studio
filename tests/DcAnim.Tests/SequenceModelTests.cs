using DcAnim.Core.Sequence;

namespace DcAnim.Tests;

public sealed class SequenceModelTests
{
    [Theory]
    [InlineData(11, 1.0, 30.0, 11)]   // native rate
    [InlineData(11, 0.5, 30.0, 22)]   // half speed holds frames
    [InlineData(11, 2.0, 30.0, 6)]    // double speed samples (round 5.5 -> 6)
    [InlineData(10, 2.0, 30.0, 5)]
    [InlineData(16, 1.0, 60.0, 32)]   // project fps scales
    [InlineData(1, 4.0, 30.0, 1)]     // never fewer than one frame
    public void ClipOutputFrames_MatchesModel(int sources, double speed, double fps, int expected) =>
        Assert.Equal(expected, SequenceTiming.ClipOutputFrames(sources, speed, fps));

    [Fact]
    public void ClipOutputFrames_RejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.ClipOutputFrames(0, 1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.ClipOutputFrames(10, 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.ClipOutputFrames(10, -1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.ClipOutputFrames(10, 1, 0));
    }

    [Fact]
    public void SourceIndex_HoldsAndSamplesDeterministically()
    {
        // Slow: 4 sources -> 8 outputs, each held twice.
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 3, 3 },
            Enumerable.Range(0, 8).Select(k => SequenceTiming.SourceIndex(k, 4, 8)).ToArray());
        // Fast: 8 sources -> 4 outputs, every other sampled.
        Assert.Equal(new[] { 0, 2, 4, 6 },
            Enumerable.Range(0, 4).Select(k => SequenceTiming.SourceIndex(k, 8, 4)).ToArray());
        // Identity.
        Assert.Equal(new[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(k => SequenceTiming.SourceIndex(k, 3, 3)).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.SourceIndex(3, 3, 3));
    }

    [Fact]
    public void StartsAndClipIndexAt_AreExact()
    {
        var starts = SequenceTiming.ClipStartFrames(new[] { 11, 22, 5 });
        Assert.Equal(new[] { 0, 11, 33 }, starts);
        Assert.Equal(0, SequenceTiming.ClipIndexAt(0, starts, new[] { 11, 22, 5 }));
        Assert.Equal(0, SequenceTiming.ClipIndexAt(10, starts, new[] { 11, 22, 5 }));
        Assert.Equal(1, SequenceTiming.ClipIndexAt(11, starts, new[] { 11, 22, 5 }));
        Assert.Equal(2, SequenceTiming.ClipIndexAt(33, starts, new[] { 11, 22, 5 }));
        Assert.Equal(2, SequenceTiming.ClipIndexAt(999, starts, new[] { 11, 22, 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceTiming.ClipIndexAt(0, Array.Empty<int>(), Array.Empty<int>()));
    }

    [Fact]
    public void InsertIndexAt_MapsDropPositionToClipBoundary()
    {
        int[] starts = { 0, 11, 33 };
        int[] counts = { 11, 22, 5 };
        // Before first midpoint -> 0; inside second clip's first half -> 1.
        Assert.Equal(0, SequenceTiming.InsertIndexAt(starts, counts, x: 0, pixelsPerFrame: 16));
        Assert.Equal(1, SequenceTiming.InsertIndexAt(starts, counts, x: 12 * 16, pixelsPerFrame: 16));
        // Past everything -> append.
        Assert.Equal(3, SequenceTiming.InsertIndexAt(starts, counts, x: 10_000, pixelsPerFrame: 16));
        Assert.Equal(0, SequenceTiming.InsertIndexAt(Array.Empty<int>(), Array.Empty<int>(), 5, 16));
    }

    [Fact]
    public void Serializer_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seq-{Guid.NewGuid():N}.dcanim");
        try
        {
            var doc = new SequenceDocument();
            doc.New("Katana Combo");
            doc.SetFps(30);
            var a = doc.AddClip("AtkKatanaA", 1.0);
            var b = doc.AddClip("AtkKatanaB2", 0.5);
            Assert.True(doc.IsDirty);

            ProjectSerializer.Save(doc.Project, path);
            var loaded = ProjectSerializer.Load(path);
            Assert.Equal("Katana Combo", loaded.Name);
            Assert.Equal(30, loaded.Fps);
            Assert.Equal(2, loaded.Clips.Count);
            Assert.Equal(a.Id, loaded.Clips[0].Id);
            Assert.Equal("AtkKatanaA", loaded.Clips[0].AnimationId);
            Assert.Equal(1.0, loaded.Clips[0].Speed);
            Assert.Equal(b.Id, loaded.Clips[1].Id);
            Assert.Equal(0.5, loaded.Clips[1].Speed);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Serializer_RejectsBadFiles()
    {
        string WriteTemp(string content)
        {
            string p = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.dcanim");
            File.WriteAllText(p, content);
            return p;
        }

        // Not JSON.
        string p1 = WriteTemp("{ nope");
        try { Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p1)); }
        finally { File.Delete(p1); }

        // Wrong format tag.
        string p2 = WriteTemp("""{"format":"nope","version":1,"name":"x","fps":30,"clips":[]}""");
        try { Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p2)); }
        finally { File.Delete(p2); }

        // Unsupported version.
        string p3 = WriteTemp("""{"format":"dcanim","version":99,"name":"x","fps":30,"clips":[]}""");
        try { Assert.Throws<NotSupportedException>(() => ProjectSerializer.Load(p3)); }
        finally { File.Delete(p3); }

        // Invalid timing.
        string p4 = WriteTemp("""{"format":"dcanim","version":1,"name":"x","fps":0,"clips":[{"id":"a","animation":"AtkKatanaB2","speed":1}]}""");
        try { Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p4)); }
        finally { File.Delete(p4); }

        // Missing file.
        Assert.Throws<InvalidDataException>(() =>
            ProjectSerializer.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dcanim")));
    }

    [Fact]
    public void Serializer_RejectsDuplicateIds()
    {
        string path = WriteTemp("dup.json",
            """{"format":"dcanim","version":2,"name":"Dup","fps":30,"clips":[{"id":"same","animation":"A","speed":1},{"id":"same","animation":"B","speed":1}],"effects":[],"audio":[]}""");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(path));
            Assert.Contains("duplicate id", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Serializer_RejectsNonFiniteNumbers()
    {
        // NaN encoded per JSON spec text (System.Text.Json rejects by default).
        string nan = WriteTemp("nan.json",
            """{"format":"dcanim","version":2,"name":"Bad","fps":30,"clips":[{"id":"c1","animation":"A","speed":1,"keyframes":[{"property":"X","frame":0,"value":NaN,"interpolation":"Hold"}]}],"effects":[],"audio":[]}""");
        try { Assert.ThrowsAny<Exception>(() => ProjectSerializer.Load(nan)); }
        finally { File.Delete(nan); }

        // Infinity transform value must fail validation, not poison the project.
        string inf = WriteTemp("inf.json",
            """{"format":"dcanim","version":2,"name":"Bad","fps":30,"clips":[{"id":"c1","animation":"A","speed":1,"transform":{"x":0,"y":0,"scaleX":1,"scaleY":1,"rotationDegrees":0,"opacity":1,"pivotX":0.5,"pivotY":"Infinity","flipH":false,"flipV":false},"keyframes":[]}],"effects":[],"audio":[]}""");
        try { Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(inf)); }
        finally { File.Delete(inf); }
    }

    private static string WriteTemp(string name, string content)
    {
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-" + name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Serializer_DetectsMissingAnimations()
    {
        var project = new SequenceProject { Name = "t" };
        project.Clips.Add(new SequenceClip { Id = "1", AnimationId = "AtkKatanaB2", Speed = 1 });
        project.Clips.Add(new SequenceClip { Id = "2", AnimationId = "Nope_Missing", Speed = 1 });

        var result = ProjectSerializer.ValidateAnimations(project, id => id == "AtkKatanaB2");
        Assert.True(result.IsValid); // missing ids are warnings, not structural errors
        Assert.Equal(new[] { "Nope_Missing" }, result.MissingAnimationIds);
    }

    [Fact]
    public void Document_MutationsTrackDirtyAndOrder()
    {
        var doc = new SequenceDocument();
        Assert.False(doc.IsDirty); // fresh
        var a = doc.AddClip("A");
        var b = doc.AddClip("B");
        var c = doc.AddClip("C", index: 1); // A C B
        Assert.True(doc.IsDirty);
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, doc.Project.Clips.Select(x => x.Id));

        doc.MoveClip(b.Id, 0); // B A C
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, doc.Project.Clips.Select(x => x.Id));

        var d = doc.DuplicateClip(a.Id); // B A A' C
        Assert.Equal(a.AnimationId, d.AnimationId);
        Assert.NotEqual(a.Id, d.Id);
        Assert.Equal(4, doc.Project.Clips.Count);

        Assert.True(doc.RemoveClip(c.Id));
        Assert.False(doc.RemoveClip("missing"));
        Assert.Equal(3, doc.Project.Clips.Count);

        doc.SetClipSpeed(a.Id, 0.5);
        Assert.Equal(0.5, doc.Project.Clips.First(x => x.Id == a.Id).Speed);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddClip("X", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetClipSpeed(a.Id, -2));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetFps(0));
        Assert.Throws<KeyNotFoundException>(() => doc.MoveClip("missing", 0));

        doc.MarkSaved("/tmp/x.dcanim");
        Assert.False(doc.IsDirty);
        Assert.Equal("/tmp/x.dcanim", doc.FilePath);
        Assert.StartsWith("Untitled", doc.DisplayName);

        doc.Rename("Combo");
        Assert.True(doc.IsDirty);
        Assert.StartsWith("*", doc.DisplayName);
        doc.MarkSaved("/tmp/combo.dcanim");
        Assert.False(doc.IsDirty);
        Assert.Equal("Combo", doc.DisplayName);
        doc.New();
        Assert.False(doc.IsDirty);
        Assert.Empty(doc.Project.Clips);
    }

    [Fact]
    public void Document_RejectsNonFiniteInput()
    {
        var doc = new SequenceDocument();
        var c = doc.AddClip("A", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetClipTransform(c.Id, Transform.Identity with { Opacity = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetClipTransform(c.Id, Transform.Identity with { X = double.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.AddKeyframe(c.Id, TransformProperty.X, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetKeyframeValue(c.Id, TransformProperty.X, 0, double.PositiveInfinity, KeyframeInterpolation.Hold));
        var e = doc.AddEffect("e.png", 0, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetEffectTransform(e.Id, Transform.Identity with { ScaleY = double.NegativeInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.AddEffectKeyframe(e.Id, TransformProperty.Opacity, 0, double.NaN));
        // Rejected edits must not create history entries: only the AddClip and
        // AddEffect above produced undo steps.
        Assert.True(doc.History.CanUndo);
        doc.Undo();
        Assert.True(doc.History.CanUndo);
        doc.Undo();
        Assert.False(doc.History.CanUndo);
        Assert.Empty(doc.Project.Clips);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void History_CappedAt100()
    {
        var doc = new SequenceDocument();
        doc.New("Cap");
        doc.AddClip("A", 1.0);
        for (int i = 0; i < 115; i++)
            doc.SetClipSpeed(doc.Project.Clips[0].Id, 1.0 + (i % 5) * 0.1);
        int undos = 0;
        while (doc.History.CanUndo && undos < 1000)
        {
            Assert.True(doc.Undo());
            undos++;
        }
        Assert.True(undos <= 101, $"history cap respected, undid {undos}");
        Assert.False(doc.Undo());
    }

    [Fact]
    public void UndoRedo_SymmetricHistory()
    {
        var doc = new SequenceDocument();
        doc.New("Sym");
        var a = doc.AddClip("A", 1.0);
        doc.SetClipSpeed(a.Id, 2.0);
        int undone = 0;
        while (doc.History.CanUndo && undone < 1000) { doc.Undo(); undone++; }
        Assert.True(undone > 0);
        int redone = 0;
        while (doc.History.CanRedo) { doc.Redo(); redone++; if (redone > 1000) break; }
        Assert.Equal(undone, redone);
        Assert.Equal(2.0, doc.Project.Clips[0].Speed);
    }

    [Fact]
    public void Validate_RejectsCorruptAndDuplicateContent()
    {
        string WriteTemp(string name, string json)
        {
            string p = Path.Combine(Path.GetTempPath(), $"asm41-{Guid.NewGuid():N}-{name}");
            File.WriteAllText(p, json);
            return p;
        }
        // Negative keyframe index.
        string p1 = WriteTemp("bad.json",
            """{"format":"dcanim","version":2,"name":"Bad","fps":30,"clips":[{"id":"c1","animation":"A","speed":1,"transform":{"x":0,"y":0,"scaleX":1,"scaleY":1,"rotationDegrees":0,"opacity":1,"pivotX":0.5,"pivotY":0.5,"flipH":false,"flipV":false},"keyframes":[{"property":"X","frame":-3,"value":1,"interpolation":"Hold"}]}],"effects":[],"audio":[]}""");
        // Duplicate clip ids.
        string p2 = WriteTemp("dup.json",
            """{"format":"dcanim","version":2,"name":"Dup","fps":30,"clips":[{"id":"same","animation":"A","speed":1},{"id":"same","animation":"B","speed":1}],"effects":[],"audio":[]}""");
        // NaN encoded per JSON spec spelling.
        string p3 = WriteTemp("nanlit.json",
            """{"format":"dcanim","version":2,"name":"Bad","fps":30,"clips":[{"id":"c1","animation":"A","speed":1,"transform":null,"keyframes":[{"property":"X","frame":0,"value":NaN,"interpolation":"Hold"}]}],"effects":[],"audio":[]}""");
        try
        {
            var ex1 = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p1));
            Assert.Contains("keyframe", ex1.Message);
            var ex2 = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p2));
            Assert.Contains("duplicate", ex2.Message);
            // System.Text.Json rejects NaN literals by default.
            Assert.ThrowsAny<Exception>(() => ProjectSerializer.Load(p3));
        }
        finally
        {
            File.Delete(p1);
            File.Delete(p2);
            File.Delete(p3);
        }
    }
}
