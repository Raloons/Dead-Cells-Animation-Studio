using DcAnim.Core.Playback;
using DcAnim.Core.Sequence;

namespace DcAnim.Tests;

/// <summary>
/// AS-M4.2 frame-level editing: trim, split, copy/paste, delete, reverse,
/// holds, ripple, v3 format, onion mapping. Pure Core logic (no game data);
/// preview/export parity is covered by the in-app audit on real animations.
/// </summary>
public sealed class SpliceTests
{
    private static SequenceDocument NewDoc()
    {
        var doc = new SequenceDocument();
        doc.New("Splice");
        return doc;
    }

    private static Func<string, int> Counter(int n) => _ => n;

    private static IReadOnlyList<ResolvedClip> Resolve(SequenceDocument doc, int n) =>
        SequenceComposer.Resolve(doc.Project, Counter(n));

    private static int TotalOf(SequenceDocument doc, int n) =>
        Resolve(doc, n).Sum(r => r.OutputFrames);

    // ---------- timing primitives ----------

    [Fact]
    public void VisibleSourceFrames_Clamps()
    {
        Assert.Equal(11, SequenceTiming.VisibleSourceFrames(11, 0, null));
        Assert.Equal(5, SequenceTiming.VisibleSourceFrames(11, 3, 7));
        Assert.Equal(1, SequenceTiming.VisibleSourceFrames(11, 10, 10));
        Assert.Equal(2, SequenceTiming.VisibleSourceFrames(11, 9, 50)); // clamps end
        Assert.Equal(1, SequenceTiming.VisibleSourceFrames(11, 50, null)); // clamps start
        Assert.Equal(0, SequenceTiming.VisibleSourceFrames(0, 0, null));
    }

    [Fact]
    public void ClipOutputFrames_IncludesHolds()
    {
        Assert.Equal(11, SequenceTiming.ClipOutputFrames(11, 1.0, 30.0, 0, 0));
        Assert.Equal(17, SequenceTiming.ClipOutputFrames(11, 1.0, 30.0, 0, 6));
        Assert.Equal(14, SequenceTiming.ClipOutputFrames(11, 1.0, 30.0, 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SequenceTiming.ClipOutputFrames(11, 1.0, 30.0, -1, 0));
    }

    [Fact]
    public void SpliceMap_ForwardAndHoldZones()
    {
        var map = new SequenceTiming.SpliceMap(3, 5, false, 0, 5, 2);
        Assert.Equal(7, map.TotalOutputs);
        Assert.Equal(3, map.SourceAt(0)); // first base frame -> first source
        Assert.Equal(7, map.SourceAt(4)); // last base frame -> last source
        Assert.Equal(7, map.SourceAt(5)); // hold zone repeats final displayed
        Assert.Equal(7, map.SourceAt(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SourceAt(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SourceAt(-1));
    }

    [Fact]
    public void SpliceMap_ReversedWithLeadHold()
    {
        // Sources 3..7 reversed, 2-frame lead hold of the first displayed (=7).
        var map = new SequenceTiming.SpliceMap(3, 5, true, 2, 5, 0);
        Assert.Equal(7, map.SourceAt(0)); // lead repeats first displayed
        Assert.Equal(7, map.SourceAt(1));
        Assert.Equal(7, map.SourceAt(2)); // base start shows last source
        Assert.Equal(3, map.SourceAt(6)); // base end shows first source
    }

    [Fact]
    public void OnionFrames_ClampNoWrap()
    {
        var (prev, next) = TimelineMath.OnionFrames(0, 10, 2);
        Assert.Empty(prev);
        Assert.Equal(new[] { 1, 2 }, next);
        (prev, next) = TimelineMath.OnionFrames(9, 10, 3);
        Assert.Equal(new[] { 6, 7, 8 }, prev); // far-to-near
        Assert.Empty(next);
        (prev, next) = TimelineMath.OnionFrames(5, 10, 2);
        Assert.Equal(new[] { 3, 4 }, prev);
        Assert.Equal(new[] { 6, 7 }, next);
        (prev, next) = TimelineMath.OnionFrames(5, 10, 99);
        Assert.Equal(3, prev.Length); // clamped to 3
        Assert.Equal(3, next.Length);
    }

    // ---------- trim ----------

    [Fact]
    public void Trim_BasicAndUndo()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        int dropped = doc.SetTrim(c.Id, 3, 7, 11);
        Assert.Equal(0, dropped);
        var r = Assert.Single(Resolve(doc, 11));
        Assert.Equal(5, r.OutputFrames);
        Assert.Equal(3, r.SourceFirst);
        Assert.Equal(5, r.SourceFrames);
        Assert.True(doc.Undo());
        r = Assert.Single(Resolve(doc, 11));
        Assert.Equal(11, r.OutputFrames);
    }

    [Fact]
    public void Trim_Validation()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetTrim(c.Id, -1, 5, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetTrim(c.Id, 5, 4, 11));
        Assert.Throws<InvalidOperationException>(() => doc.SetTrim(c.Id, 11, 12, 11));
        // Rejected edits leave no history: the only entry is the AddClip.
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
        Assert.Empty(doc.Project.Clips);
    }

    [Fact]
    public void Trim_ToOneFrame()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.SetTrim(c.Id, 7, 7, 11);
        var r = Assert.Single(Resolve(doc, 11));
        Assert.Equal(1, r.OutputFrames);
        Assert.Equal(7, r.Map.SourceAt(0));
    }

    [Fact]
    public void Trim_RemapsKeysToSources()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 0, 0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 5, 12);
        doc.AddKeyframe(c.Id, TransformProperty.X, 10, 99);
        int dropped = doc.SetTrim(c.Id, 4, 8, 11);
        Assert.Equal(2, dropped); // sources 0 and 10 left the visible range
        var keys = doc.Project.Clips[0].Keyframes;
        var kept = Assert.Single(keys);
        Assert.Equal(TransformProperty.X, kept.Property);
        Assert.Equal(1, kept.Frame); // source 5 now shows at local 1
        Assert.Equal(12, kept.Value);
    }

    // ---------- reverse & hold ----------

    [Fact]
    public void Reverse_MirrorsKeysAndUndo()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 2, 9);
        doc.SetReversed(c.Id, true, 11);
        var r = Assert.Single(Resolve(doc, 11));
        Assert.True(r.Reversed);
        Assert.Equal(11, r.OutputFrames);
        Assert.Equal(8, Assert.Single(doc.Project.Clips[0].Keyframes).Frame); // 11-1-2
        Assert.Equal(10, r.Map.SourceAt(0)); // base start shows last source
        Assert.Equal(0, r.Map.SourceAt(10));
        Assert.True(doc.Undo());
        Assert.False(doc.Project.Clips[0].Reversed);
        Assert.Equal(2, Assert.Single(doc.Project.Clips[0].Keyframes).Frame);
    }

    [Fact]
    public void Hold_AppendsIdenticalFrames()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        int dropped = doc.SetTailHold(c.Id, 6, 11);
        Assert.Equal(0, dropped);
        var r = Assert.Single(Resolve(doc, 11));
        Assert.Equal(17, r.OutputFrames);
        Assert.Equal(6, r.TailHoldFrames);
        for (int k = 11; k < 17; k++)
            Assert.Equal(10, r.Map.SourceAt(k)); // final pose held
        Assert.Equal(0, r.Map.SourceAt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetTailHold(c.Id, -1, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.SetTailHold(c.Id, SequenceClip.MaxHoldFrames + 1, 11));
        Assert.True(doc.Undo());
        Assert.Equal(11, Resolve(doc, 11)[0].OutputFrames);
    }

    // ---------- split ----------

    [Fact]
    public void Split_ExactAt1xWithContinuousMotion()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 0, 0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 5, 12);
        doc.AddKeyframe(c.Id, TransformProperty.ScaleX, 2, 1, KeyframeInterpolation.Linear);
        doc.AddKeyframe(c.Id, TransformProperty.ScaleX, 8, 3, KeyframeInterpolation.Linear);
        var before = doc.Project.Clips[0].Keyframes.ToList();

        var result = doc.SplitAt(5, Counter(11));
        Assert.NotNull(result);
        var resolved = Resolve(doc, 11);
        Assert.Equal(2, resolved.Count);
        // Exact visible sequence: sources 0..10 in order across the halves.
        var shown = resolved.SelectMany(r =>
            Enumerable.Range(0, r.OutputFrames).Select(r.Map.SourceAt)).ToList();
        Assert.Equal(Enumerable.Range(0, 11), shown);
        // Evaluated motion identical at every output frame.
        for (int g = 0; g < 11; g++)
        {
            var r = resolved.First(x => g >= x.StartFrame && g < x.StartFrame + x.OutputFrames);
            int local = g - r.StartFrame;
            foreach (var p in new[] { TransformProperty.X, TransformProperty.ScaleX })
            {
                double expected = KeyframeEvaluator.Evaluate(before, p, g, Transform.Identity.Get(p));
                double actual = KeyframeEvaluator.Evaluate(r.Clip.Keyframes, p, local, Transform.Identity.Get(p));
                Assert.Equal(expected, actual, precision: 9);
            }
        }
        // Selection-friendly: left keeps the id, right is new.
        Assert.Equal(c.Id, result!.LeftId);
        Assert.NotEqual(result.LeftId, result.RightId);
        Assert.True(doc.Undo());
        Assert.Single(doc.Project.Clips);
    }

    [Fact]
    public void Split_BoundariesAreNoOps()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        Assert.Null(doc.SplitAt(0, Counter(11))); // at clip start
        Assert.Null(doc.SplitAt(11, Counter(11))); // past the end
        Assert.Null(doc.SplitAt(999, Counter(11))); // outside
        Assert.Single(doc.Project.Clips);
        // No-op splits leave no history: only the AddClip entry exists.
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
        var empty = NewDoc();
        Assert.Null(empty.SplitAt(0, Counter(11)));
    }

    [Fact]
    public void Split_SpeedRoundingStaysBounded()
    {
        var doc = NewDoc();
        doc.AddClip("A", 0.5); // 10 sources -> 20 outputs
        var result = doc.SplitAt(7, _ => 10);
        Assert.NotNull(result);
        Assert.InRange(TotalOf(doc, 10), 19, 21);
    }

    // ---------- delete / copy / paste ----------

    [Fact]
    public void DeleteRange_MiddleSplitsClip()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 1, 5);
        doc.AddKeyframe(c.Id, TransformProperty.X, 9, 50);
        int dropped = doc.DeleteRange(3, 7, Counter(11));
        Assert.Equal(2, doc.Project.Clips.Count);
        Assert.Equal(7, TotalOf(doc, 11));
        // Survivors show sources 0-2 and 7-10.
        var shown = Resolve(doc, 11).SelectMany(r =>
            Enumerable.Range(0, r.OutputFrames).Select(r.Map.SourceAt)).ToList();
        Assert.Equal(new[] { 0, 1, 2, 7, 8, 9, 10 }, shown);
        Assert.True(doc.Undo());
        Assert.Single(doc.Project.Clips);
        Assert.Equal(11, TotalOf(doc, 11));
    }

    [Fact]
    public void DeleteRange_FullClipAndEmptySafe()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.DeleteRange(0, 11, Counter(11));
        Assert.Empty(doc.Project.Clips);
        var empty = NewDoc();
        Assert.Equal(0, empty.DeleteRange(0, 5, Counter(11)));
        Assert.Empty(empty.CopyRange(0, 5, Counter(11)));
        Assert.Empty(empty.PasteSegments(0, Array.Empty<SequenceDocument.SpliceSegment>(), Counter(11)));
    }

    [Fact]
    public void CopyPaste_RoundTrip()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.AddKeyframe(c.Id, TransformProperty.X, 3, 42);
        var segments = doc.CopyRange(2, 9, Counter(11));
        var seg = Assert.Single(segments);
        Assert.Equal("A", seg.AnimationId);
        Assert.Equal(2, seg.SourceFirst);
        Assert.Equal(8, seg.SourceLast);
        Assert.Equal(42, Assert.Single(seg.Keyframes).Value);
        Assert.Equal(1, Assert.Single(seg.Keyframes).Frame); // rebased: 3 -> 1

        var created = doc.PasteSegments(0, segments, Counter(11));
        Assert.Single(created);
        Assert.Equal(11 + 7, TotalOf(doc, 11));
        Assert.True(doc.Undo()); // paste is one step
        Assert.Equal(11, TotalOf(doc, 11));
    }

    [Fact]
    public void CopyRange_SpansAnimationsInOrder()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        doc.AddClip("B", 1.0);
        var segments = doc.CopyRange(9, 13, _ => 11); // tail of A + head of B
        Assert.Equal(2, segments.Count);
        Assert.Equal("A", segments[0].AnimationId);
        Assert.Equal("B", segments[1].AnimationId);
        Assert.Equal(9, segments[0].SourceFirst);
        Assert.Equal(10, segments[0].SourceLast);
        Assert.Equal(0, segments[1].SourceFirst);
        Assert.Equal(1, segments[1].SourceLast);
    }

    [Fact]
    public void Paste_SplitsHostAndRipples()
    {
        var doc = NewDoc();
        var a = doc.AddClip("A", 1.0);
        doc.AddAudio("s.wav", 0.5);
        var segments = doc.CopyRange(0, 3, Counter(11));
        int totalBefore = TotalOf(doc, 11);
        doc.BeginGroup("Paste test");
        var created = doc.PasteSegments(5, segments, Counter(11));
        // Ripple: audio at 0.5s (= frame 15 @30fps) is at/after frame 5 -> shifts +3.
        doc.ShiftFxAudio(5, 3);
        doc.EndGroup();
        Assert.Single(created);
        Assert.Equal(3, doc.Project.Clips.Count); // left, pasted, right
        Assert.Equal(totalBefore + 3, TotalOf(doc, 11));
        var audio = Assert.Single(doc.Project.Audio);
        Assert.Equal(0.5 + 3.0 / 30.0, audio.StartSeconds, precision: 9);
        Assert.True(doc.Undo()); // group = one step
        Assert.Single(doc.Project.Clips);
        Assert.Equal(0.5, doc.Project.Audio[0].StartSeconds, precision: 9);
    }

    [Fact]
    public void Paste_RejectsGarbage()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        var bad = new SequenceDocument.SpliceSegment("A", 5, 2, 1.0, false, 0, 0,
            new List<TransformKeyframe>());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.PasteSegments(0, new[] { bad }, Counter(11)));
        var badSpeed = new SequenceDocument.SpliceSegment("A", 0, 2, 0, false, 0, 0,
            new List<TransformKeyframe>());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.PasteSegments(0, new[] { badSpeed }, Counter(11)));
    }

    // ---------- groups & ripple ----------

    [Fact]
    public void Group_IsASingleUndoStep()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.BeginGroup("Trim+reverse");
        doc.SetTrim(c.Id, 2, 8, 11);
        doc.SetReversed(c.Id, true, 11);
        doc.EndGroup();
        Assert.True(doc.Undo()); // whole group reverts at once...
        var clip = Assert.Single(doc.Project.Clips);
        Assert.Equal(0, clip.SourceFirst);
        Assert.Null(clip.SourceLast);
        Assert.False(clip.Reversed);
        Assert.True(doc.History.CanUndo); // ...leaving the earlier AddClip entry
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
    }

    [Fact]
    public void Group_CannotNest()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        doc.BeginGroup("Outer");
        Assert.Throws<InvalidOperationException>(() => doc.BeginGroup("Inner"));
        doc.AbortGroup();
        // Pre-group state restored; only the AddClip entry remains.
        Assert.Single(doc.Project.Clips);
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
    }

    [Fact]
    public void ShiftFxAudio_MovesOnlyAtOrAfterPoint()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        var e1 = doc.AddEffect("x.png", 2, 5);
        var e2 = doc.AddEffect("y.png", 10, 5);
        doc.AddAudio("s.wav", 1.0); // frame 30 @30fps
        doc.ShiftFxAudio(5, 4);
        Assert.Equal(2, doc.Project.Effects.First(e => e.Id == e1.Id).StartFrame);
        Assert.Equal(14, doc.Project.Effects.First(e => e.Id == e2.Id).StartFrame);
        Assert.Equal(1.0 + 4.0 / 30.0, doc.Project.Audio[0].StartSeconds, precision: 9);
        doc.ShiftFxAudio(0, 0);
        Assert.True(doc.History.CanUndo);
        doc.Undo();
        Assert.Equal(10, doc.Project.Effects.First(e => e.Id == e2.Id).StartFrame);
    }

    [Fact]
    public void GroupedOps_UndoInOrder()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        var b = doc.AddClip("B", 1.0);
        doc.BeginGroup("Split clip");
        var split = doc.SplitAt(2, _ => 11);
        doc.EndGroup();
        Assert.NotNull(split);
        Assert.Equal(3, doc.Project.Clips.Count);
        doc.BeginGroup("Reverse clip");
        doc.SetReversed(b.Id, true, 11);
        doc.EndGroup();
        doc.BeginGroup("Hold 6f");
        doc.SetTailHold(b.Id, 6, 11);
        doc.EndGroup();
        Assert.Equal(11 + 11 + 6, SequenceComposer.Resolve(doc.Project, _ => 11).Sum(r => r.OutputFrames));
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Project.Clips.Count);
    }

    [Fact]
    public void Group_NoOpLeavesNoHistory()
    {
        var doc = NewDoc();
        doc.AddClip("A", 1.0);
        doc.BeginGroup("No-op split");
        var split = doc.SplitAt(0, _ => 11); // at clip start: no-op
        doc.EndGroupIfChanged();
        Assert.Null(split);
        // Only the AddClip entry remains.
        Assert.True(doc.Undo());
        Assert.False(doc.History.CanUndo);
    }

    // ---------- format ----------

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeadCells.AnimationStudio.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void DemoProject_LoadsAndRoundTrips()
    {
        string path = Path.Combine(FindRepoRoot(), "docs", "evidence", "katana-splice.dcanim");
        var loaded = ProjectSerializer.Load(path);
        Assert.Equal(3, loaded.Version);
        Assert.Equal(4, loaded.Clips.Count);
        Assert.Equal("AtkKatanaA", loaded.Clips[0].AnimationId);
        Assert.Equal(0, loaded.Clips[0].SourceFirst);
        Assert.Equal(3, loaded.Clips[0].SourceLast);
        Assert.True(loaded.Clips[2].Reversed);
        Assert.Equal(6, loaded.Clips[3].TailHoldFrames);
        Assert.True(loaded.RippleFxAudio);
        string tmp = TempPath("demo-roundtrip.dcanim");
        try
        {
            ProjectSerializer.Save(loaded, tmp);
            var reloaded = ProjectSerializer.Load(tmp);
            Assert.Equal(loaded.Clips.Count, reloaded.Clips.Count);
            Assert.Equal(loaded.Clips[1].Keyframes.Count, reloaded.Clips[1].Keyframes.Count);
            Assert.True(reloaded.Clips[2].Reversed);
        }
        finally { File.Delete(tmp); }
    }

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"asm42-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void Serializer_V3RoundTrip()
    {
        var doc = NewDoc();
        var c = doc.AddClip("AtkKatanaA", 1.25);
        doc.SetTrim(c.Id, 1, 9, 26);
        doc.SetReversed(c.Id, true, 26);
        doc.SetTailHold(c.Id, 6, 26);
        doc.AddKeyframe(c.Id, TransformProperty.X, 2, 4);
        string path = TempPath("v3.dcanim");
        try
        {
            ProjectSerializer.Save(doc.Project, path);
            var loaded = ProjectSerializer.Load(path);
            Assert.Equal(3, loaded.Version);
            var lc = Assert.Single(loaded.Clips);
            Assert.Equal(1, lc.SourceFirst);
            Assert.Equal(9, lc.SourceLast);
            Assert.True(lc.Reversed);
            Assert.Equal(6, lc.TailHoldFrames);
            Assert.Equal(1.25, lc.Speed);
            Assert.Equal(4, Assert.Single(lc.Keyframes).Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Serializer_MigratesV1V2WithDefaults()
    {
        string path = TempPath("legacy.dcanim");
        try
        {
            File.WriteAllText(path,
                """{"format":"dcanim","version":1,"name":"Legacy","fps":30,"clips":[{"id":"c1","animation":"A","speed":1}]}""");
            var loaded = ProjectSerializer.Load(path);
            Assert.Equal(3, loaded.Version);
            var lc = Assert.Single(loaded.Clips);
            Assert.Equal(0, lc.SourceFirst);
            Assert.Null(lc.SourceLast);
            Assert.False(lc.Reversed);
            Assert.Equal(0, lc.LeadHoldFrames);
            Assert.Equal(0, lc.TailHoldFrames);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Serializer_RejectsBadSpliceFields()
    {
        string WriteTemp(string json)
        {
            string p = TempPath(Guid.NewGuid() + ".dcanim");
            File.WriteAllText(p, json);
            return p;
        }
        string ok(string trim) =>
            $$"""{"format":"dcanim","version":3,"name":"T","fps":30,"clips":[{"id":"c1","animation":"A","speed":1{{trim}}}],"effects":[],"audio":[]}""";
        string[] bad =
        {
            ok(""","sourceFirst":-1"""),
            ok(""","sourceFirst":5,"sourceLast":4"""),
            ok(""","tailHoldFrames":10001"""),
            ok(""","leadHoldFrames":-2"""),
        };
        foreach (string json in bad)
        {
            string p = WriteTemp(json);
            try { Assert.Throws<InvalidDataException>(() => ProjectSerializer.Load(p)); }
            finally { File.Delete(p); }
        }
        string p99 = WriteTemp(
            """{"format":"dcanim","version":99,"name":"T","fps":30,"clips":[]}""");
        try { Assert.Throws<NotSupportedException>(() => ProjectSerializer.Load(p99)); }
        finally { File.Delete(p99); }
    }

    [Fact]
    public void ClampFrameRanges_HandlesDrift()
    {
        var doc = NewDoc();
        var c = doc.AddClip("A", 1.0);
        doc.SetTrim(c.Id, 2, 8, 26);
        // Game updated: only 5 frames now.
        int clamped = ProjectSerializer.ClampFrameRanges(doc.Project, _ => 5);
        Assert.Equal(1, clamped);
        Assert.Equal(2, doc.Project.Clips[0].SourceFirst);
        Assert.Equal(4, doc.Project.Clips[0].SourceLast);
        Assert.Equal(0, ProjectSerializer.ClampFrameRanges(doc.Project, _ => 5));
        // Unknown animations are skipped, not fatal.
        doc.AddClip("Missing", 1.0);
        Assert.Equal(0, ProjectSerializer.ClampFrameRanges(doc.Project, id => id == "A" ? 5 : throw new KeyNotFoundException()));
    }

    // ---------- resolve ----------

    [Fact]
    public void Resolve_TrimsReverseHoldsAndSkips()
    {
        var doc = NewDoc();
        var a = doc.AddClip("A", 1.0);
        doc.SetTrim(a.Id, 2, 6, 11); // 5 sources -> 5 outputs
        doc.SetTailHold(a.Id, 3, 11);
        var b = doc.AddClip("B", 1.0);
        doc.SetReversed(b.Id, true, 8);
        var r = SequenceComposer.Resolve(doc.Project, id => id == "A" ? 11 : 8);
        Assert.Equal(2, r.Count);
        Assert.Equal(8, r[0].OutputFrames); // 5 + 3 hold
        Assert.Equal(2, r[0].SourceFirst);
        Assert.True(r[1].Reversed);
        Assert.Equal(7, r[1].Map.SourceAt(0)); // reversed base start shows last source
        Assert.Equal(8, r[1].StartFrame);
        // Missing animation is skipped, never fatal.
        doc.AddClip("Nope", 1.0);
        var r2 = SequenceComposer.Resolve(doc.Project, id =>
            id == "Nope" ? throw new KeyNotFoundException() : id == "A" ? 11 : 8);
        Assert.Equal(2, r2.Count);
    }
}
