using System.Text;
using DcAnim.Core.Sequence;
using DcAnim.Core.Sources;

namespace DcAnim.Tests;

public sealed class AuthoringModelTests
{
    [Fact]
    public void Keyframes_HoldAndLinear()
    {
        var keys = new List<TransformKeyframe>
        {
            new(TransformProperty.X, 0, 10, KeyframeInterpolation.Hold),
            new(TransformProperty.X, 10, 20, KeyframeInterpolation.Linear),
            new(TransformProperty.X, 20, 40, KeyframeInterpolation.Hold),
        };
        // The key AT frame 0 applies at frame 0; before any key the base holds.
        Assert.Equal(7, KeyframeEvaluator.Evaluate(
            new List<TransformKeyframe> { new(TransformProperty.X, 5, 99) }, TransformProperty.X, 0, 7));
        // Hold segment keeps latest key.
        Assert.Equal(10, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 0, 0));
        // Hold segment keeps latest key; the key AT frame 10 already applies there.
        Assert.Equal(10, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 5, 0));
        Assert.Equal(10, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 9, 0));
        Assert.Equal(20, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 10, 0));
        // Linear segment interpolates.
        Assert.Equal(30, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 15, 0));
        Assert.Equal(40, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 20, 0));
        // Past the end holds the last key.
        Assert.Equal(40, KeyframeEvaluator.Evaluate(keys, TransformProperty.X, 99, 0));
        // Other properties fall back to base.
        Assert.Equal(7, KeyframeEvaluator.Evaluate(keys, TransformProperty.Y, 15, 7));
    }

    [Fact]
    public void Keyframes_EvaluateAllCoversEveryProperty()
    {
        var keys = new List<TransformKeyframe>
        {
            new(TransformProperty.Opacity, 5, 0.5),
            new(TransformProperty.RotationDegrees, 5, 90),
        };
        var t = KeyframeEvaluator.EvaluateAll(keys, 5, Transform.Identity);
        Assert.Equal(0.5, t.Opacity);
        Assert.Equal(90, t.RotationDegrees);
        Assert.Equal(1.0, t.ScaleX); // untouched
        Assert.Equal(0.5, t.PivotX);
    }

    [Fact]
    public void TransformLayout_IdentityIsTopLeft()
    {
        // Matches the compositor: identity draws the native frame at the origin.
        var r = TransformLayout.MapRect(150, 150, Transform.Identity, 150, 150);
        Assert.Equal((0, 0, 150, 150), r);
        Assert.Equal((75, 75), TransformLayout.PivotPoint(Transform.Identity, 150, 150));
    }

    [Fact]
    public void TransformLayout_TranslationAndFlip()
    {
        var t = Transform.Identity with { X = 8, Y = 4, FlipH = true };
        var r = TransformLayout.MapRect(16, 16, t, 32, 32);
        // Pivot lands on canvas center + position; flip mirrors about it.
        Assert.Equal((16, 12, 16, 16), (Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height)));
        Assert.Equal((24, 20), TransformLayout.PivotPoint(t, 32, 32));
    }

    [Fact]
    public void TransformLayout_RotationGrowsBounds()
    {
        var r = TransformLayout.MapRect(10, 10, Transform.Identity with { RotationDegrees = 45 }, 100, 100);
        // 10x10 rotated 45° about its center: AABB ≈ 14.14 x 14.14.
        Assert.InRange(r.Width, 14.1, 14.2);
        Assert.InRange(r.Height, 14.1, 14.2);
    }

    [Fact]
    public void Document_UndoRedoRestoresExactState()
    {
        var doc = new SequenceDocument();
        doc.New("T");
        var a = doc.AddClip("A");
        doc.SetClipSpeed(a.Id, 0.5);
        Assert.True(doc.IsDirty);

        Assert.True(doc.Undo()); // undo speed
        Assert.Equal(1.0, doc.Project.Clips[0].Speed);
        Assert.True(doc.Undo()); // undo add
        Assert.Empty(doc.Project.Clips);
        Assert.False(doc.IsDirty); // back at saved-equivalent empty state? (see below)

        Assert.True(doc.Redo());
        Assert.Single(doc.Project.Clips);
        Assert.True(doc.Redo());
        Assert.Equal(0.5, doc.Project.Clips[0].Speed);
        Assert.False(doc.CanRedoSafe());
    }

    [Fact]
    public void Document_UndoToSavedStateClearsDirty()
    {
        var doc = new SequenceDocument();
        doc.New("T");
        doc.AddClip("A");
        doc.MarkSavedForTest();
        doc.AddClip("B");
        Assert.True(doc.IsDirty);
        Assert.True(doc.Undo());
        Assert.False(doc.IsDirty); // restored exactly the saved snapshot
        Assert.Single(doc.Project.Clips);
    }

    [Fact]
    public void Document_GroupingAndKeyframeOps()
    {
        var doc = new SequenceDocument();
        doc.New("T");
        var a = doc.AddClip("A");
        doc.SetClipTransform(a.Id, Transform.Identity with { X = 10 });
        Assert.Equal(10, doc.Project.Clips[0].BaseTransform.X);
        var key = doc.AddKeyframe(a.Id, TransformProperty.X, 3, 30);
        Assert.Equal(3, key.Frame);
        Assert.Equal(40, doc.AddKeyframe(a.Id, TransformProperty.X, 3, 40).Value); // replace same frame
        Assert.Single(doc.Project.Clips[0].Keyframes.Where(k => k.Property == TransformProperty.X && k.Frame == 3));
        Assert.True(doc.MoveKeyframe(a.Id, TransformProperty.X, 3, 5));
        Assert.True(doc.RemoveKeyframe(a.Id, TransformProperty.X, 5));
        Assert.False(doc.RemoveKeyframe(a.Id, TransformProperty.X, 5));

        // Grouped drag: one undo step.
        doc.BeginDrag("Drag transform");
        doc.SetClipTransform(a.Id, Transform.Identity with { X = 1 });
        doc.SetClipTransform(a.Id, Transform.Identity with { X = 2 });
        doc.EndDrag();
        Assert.Equal(2, doc.Project.Clips[0].BaseTransform.X);
        Assert.True(doc.Undo());
        Assert.Equal(10, doc.Project.Clips[0].BaseTransform.X, precision: 9);
    }

    [Fact]
    public void Document_EffectAndAudioOps()
    {
        var doc = new SequenceDocument();
        doc.New("T");
        var e = doc.AddEffect("fx.png", startFrame: 10, durationFrames: 20);
        Assert.Equal(10, e.StartFrame);
        doc.MoveEffect(e.Id, 12);
        doc.ResizeEffect(e.Id, 25);
        doc.SetEffectLayer(e.Id, 2);
        doc.SetEffectTransform(e.Id, Transform.Identity with { ScaleX = 2 });
        doc.AddEffectKeyframe(e.Id, TransformProperty.Opacity, 3, 0.5);
        Assert.True(doc.RemoveEffectKeyframe(e.Id, TransformProperty.Opacity, 3));
        Assert.True(doc.RemoveEffect(e.Id));
        Assert.False(doc.RemoveEffect(e.Id));

        var au = doc.AddAudio("s.wav", startSeconds: 1.5);
        doc.MoveAudio(au.Id, 2.0);
        doc.TrimAudio(au.Id, 0.25, 3.0);
        doc.SetAudioVolume(au.Id, 0.5);
        doc.SetAudioMuted(au.Id, true);
        Assert.Equal(0.5, doc.Project.Audio[0].Volume);
        Assert.True(doc.Project.Audio[0].Muted);
        Assert.True(doc.RemoveAudio(au.Id));
    }

    [Fact]
    public void Document_VersionValidationRejectsDuplicates()
    {
        var doc = new SequenceDocument();
        doc.New("T");
        var a = doc.AddClip("A");
        // Force a duplicate id via undo-stack surgery is not public; validate directly.
        var project = new SequenceProject { Name = "x" };
        project.Clips.Add(new SequenceClip { Id = "dup", AnimationId = "A", Speed = 1 });
        project.Clips.Add(new SequenceClip { Id = "dup", AnimationId = "B", Speed = 1 });
        var v = ProjectSerializer.Validate(project);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Serializer_V1LoadsAsV2WithDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v1-{Guid.NewGuid():N}.dcanim");
        try
        {
            File.WriteAllText(path,
                """{"format":"dcanim","version":1,"name":"Old","fps":24,"clips":[{"id":"c1","animation":"A","speed":2}]}""");
            var project = ProjectSerializer.Load(path);
            Assert.Equal(SequenceProject.CurrentVersion, project.Version);
            Assert.Equal("Old", project.Name);
            Assert.Equal(24, project.Fps);
            var clip = Assert.Single(project.Clips);
            Assert.Equal("A", clip.AnimationId);
            Assert.Equal(2, clip.Speed);
            Assert.Equal(Transform.Identity, clip.BaseTransform);
            Assert.Empty(clip.Keyframes);
            Assert.Empty(project.Effects);
            Assert.Empty(project.Audio);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Serializer_V2RoundTripPreservesAuthoring()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v2-{Guid.NewGuid():N}.dcanim");
        string path2 = Path.Combine(Path.GetTempPath(), $"v2b-{Guid.NewGuid():N}.dcanim");
        try
        {
            var project = new SequenceProject { Name = "Auth", Fps = 30 };
            project.Clips.Add(new SequenceClip
            {
                Id = "c1", AnimationId = "A", Speed = 0.5,
                BaseTransform = Transform.Identity with { X = 5, FlipH = true },
                Keyframes = new List<TransformKeyframe> { new(TransformProperty.Y, 2, 9, KeyframeInterpolation.Linear) },
            });
            project.Effects.Add(new EffectClip
            {
                Id = "e1", Name = "Boom", ImagePath = "fx/boom.png",
                StartFrame = 4, DurationFrames = 10, Layer = 1,
                BaseTransform = Transform.Identity with { Opacity = 0.5 },
                Keyframes = new List<TransformKeyframe> { new(TransformProperty.ScaleX, 0, 2) },
            });
            project.Audio.Add(new AudioClip
            {
                Id = "a1", Name = "Hit", AudioPath = "sfx/hit.wav",
                StartSeconds = 1.25, TrimStartSeconds = 0.1, DurationSeconds = 2.0,
                Volume = 0.8, Muted = true,
            });
            ProjectSerializer.Save(project, path);
            var loaded = ProjectSerializer.Load(path);
            Assert.Equal("Auth", loaded.Name);
            var clip = Assert.Single(loaded.Clips);
            Assert.Equal(5, clip.BaseTransform.X);
            Assert.True(clip.BaseTransform.FlipH);
            var key = Assert.Single(clip.Keyframes);
            Assert.Equal((TransformProperty.Y, 2, 9, KeyframeInterpolation.Linear),
                (key.Property, key.Frame, key.Value, key.Interpolation));
            var fx = Assert.Single(loaded.Effects);
            Assert.Equal(4, fx.StartFrame);
            // Relative references resolve against the project directory on load…
            Assert.Equal("boom.png", Path.GetFileName(fx.ImagePath));
            Assert.True(Path.IsPathRooted(fx.ImagePath));
            // …and relativize again on save (portable forward slashes).
            ProjectSerializer.Save(loaded, path2);
            Assert.Contains("\"image\": \"fx/boom.png\"", File.ReadAllText(path2));
            var au = Assert.Single(loaded.Audio);
            Assert.Equal(1.25, au.StartSeconds);
            Assert.Equal(0.8, au.Volume);
            Assert.True(au.Muted);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path2))
                File.Delete(path2);
        }
    }

    [Fact]
    public void Serializer_RelativePathsResolveAgainstProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string img = Path.Combine(dir, "shot.png");
            File.WriteAllBytes(img, new byte[] { 1, 2, 3 });
            string path = Path.Combine(dir, "p.dcanim");
            var project = new SequenceProject { Name = "R" };
            project.Effects.Add(new EffectClip { Id = "e1", ImagePath = img, StartFrame = 0, DurationFrames = 5 });
            ProjectSerializer.Save(project, path);
            string json = File.ReadAllText(path);
            Assert.Contains("\"image\": \"shot.png\"", json);
            var loaded = ProjectSerializer.Load(path);
            Assert.Equal(img, loaded.Effects[0].ImagePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AudioTimeline_SchedulesAudibleSpans()
    {
        var clips = new List<AudioClip>
        {
            new() { Id = "a", Name = "A", AudioPath = "a.wav", StartSeconds = 1.0, TrimStartSeconds = 0.5, DurationSeconds = 2.0, Volume = 0.8 },
            new() { Id = "b", Name = "B", AudioPath = "b.wav", StartSeconds = 5.0, Muted = true },
        };
        static double? Len(string p) => 10.0;

        Assert.Empty(AudioTimeline.ActiveAt(clips, 0.5, Len));
        var at1 = Assert.Single(AudioTimeline.ActiveAt(clips, 1.0, Len));
        Assert.Equal(0.5, at1.FileOffsetSeconds, precision: 9);
        var at2 = Assert.Single(AudioTimeline.ActiveAt(clips, 2.0, Len));
        Assert.Equal(1.5, at2.FileOffsetSeconds, precision: 9);
        Assert.Empty(AudioTimeline.ActiveAt(clips, 3.0, Len)); // duration elapsed
        Assert.Empty(AudioTimeline.ActiveAt(clips, 6.0, Len)); // muted
    }

    [Fact]
    public void AudioTimeline_ClampsToFileEnd()
    {
        var clips = new List<AudioClip>
        {
            new() { Id = "a", Name = "A", AudioPath = "a.wav", StartSeconds = 0.0, TrimStartSeconds = 8.0 },
        };
        static double? Len(string p) => 10.0;
        Assert.Single(AudioTimeline.ActiveAt(clips, 0.0, Len));
        Assert.Single(AudioTimeline.ActiveAt(clips, 1.9, Len));
        Assert.Empty(AudioTimeline.ActiveAt(clips, 2.0, Len)); // 8 + 2 = EOF
    }
}

internal static class DocumentTestHooks
{
    public static bool CanRedoSafe(this SequenceDocument doc) => doc.History.CanRedo;
    public static void MarkSavedForTest(this SequenceDocument doc) =>
        doc.MarkSaved("/tmp/probe.dcanim");
}
