using DcAnim.Core.Catalog;
using DcAnim.Core.Model;

namespace DcAnim.Tests;

public sealed class AnimationCatalogTests
{
    [Fact]
    public void Constructor_GroupsByStemAndOrdersNumerically()
    {
        var catalog = new AnimationCatalog(new[] { "atk_10", "atk_02", "atk_01", "idle_00", "lone" });

        Assert.Equal(3, catalog.Count);
        Assert.Equal(new[] { "atk_01", "atk_02", "atk_10" }, catalog.GetFrameRegions("atk"));
        Assert.Equal(new[] { "idle_00" }, catalog.GetFrameRegions("idle"));
        Assert.Equal(new[] { "lone" }, catalog.GetFrameRegions("lone"));
    }

    [Fact]
    public void GetFrameRegions_UnknownId_Throws()
    {
        var catalog = new AnimationCatalog(new[] { "atk_00" });
        Assert.Throws<KeyNotFoundException>(() => catalog.GetFrameRegions("nope"));
        Assert.False(catalog.Contains("nope"));
        Assert.True(catalog.Contains("atk"));
    }

    [Fact]
    public void Search_IsCaseInsensitiveSubstring()
    {
        var catalog = new AnimationCatalog(new[] { "AtkKatanaB2_00", "AtkKatanaB3_00", "idle_00" });

        var hits = catalog.Search("katana").Select(a => a.Id).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "AtkKatanaB2", "AtkKatanaB3" }, hits);
        Assert.Equal(3, catalog.Search(null).Count());
        Assert.Equal(3, catalog.Search("").Count());
    }

    [Fact]
    public void GetNumberingGaps_ReportsMissingFrames()
    {
        var catalog = new AnimationCatalog(new[] { "atk_00", "atk_01", "atk_03" });

        Assert.Equal(new[] { 2 }, catalog.GetNumberingGaps("atk"));
        Assert.Empty(new AnimationCatalog(new[] { "atk_00" }).GetNumberingGaps("atk"));
    }

    [Fact]
    public void LoadTimingOverlay_SkipsNonObjectEntries()
    {
        string json = """
            {
              "_note": "documentation string, not timing",
              "AtkKatanaB2": { "hitFrame": 6, "charge": 0.2, "animSpd": 1, "fxId": "fxBeheadedKatanaB2", "source": "test" }
            }
            """;
        string path = Path.Combine(Path.GetTempPath(), $"timing-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var overlay = DcAnim.Core.Sources.AnimationLoader.LoadTimingOverlay(path);
            Assert.True(overlay.ContainsKey("AtkKatanaB2"));
            Assert.False(overlay.ContainsKey("_note"));
            Assert.Equal(6, overlay["AtkKatanaB2"].HitFrame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_AppliesTimingOverlay()
    {
        var timing = new Dictionary<string, AnimationTiming>
        {
            ["atk"] = new AnimationTiming(HitFrame: 6, ChargeSeconds: 0.2, AnimSpeed: 1, FxId: "fxX", SourceNote: "test"),
        };
        var catalog = new AnimationCatalog(new[] { "atk_00", "atk_01" }, timing);

        var desc = catalog.Animations.Single(a => a.Id == "atk");
        Assert.Equal(6, desc.HitFrame);
    }
}

public sealed class PlaybackEngineTests
{
    private static DcAnim.Core.Model.AnimationClip MakeClip(params double[] durations)
    {
        var frames = durations.Select((d, i) => new ClipFrame
        {
            Index = i,
            RegionName = $"f_{i:00}",
            Width = 8,
            Height = 8,
            Rgba = new byte[8 * 8 * 4],
            Duration = TimeSpan.FromSeconds(d),
        }).ToList();
        return new AnimationClip { Id = "test", Frames = frames, SourceDescription = "unit" };
    }

    [Fact]
    public void Load_SetsFirstFrame_AndPauses()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine();
        engine.Load(MakeClip(0.1, 0.1));

        Assert.Equal(0, engine.CurrentFrameIndex);
        Assert.False(engine.IsPlaying);
        Assert.Equal(2, engine.FrameCount);
    }

    [Fact]
    public void Load_EmptyClip_Throws()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine();
        Assert.Throws<ArgumentException>(() => engine.Load(MakeClip()));
    }

    [Fact]
    public void Tick_AdvancesByFrameDurations()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.1, 0.1, 0.1));
        engine.Play();

        engine.Tick(TimeSpan.FromSeconds(0.15));
        Assert.Equal(1, engine.CurrentFrameIndex);
        engine.Tick(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, engine.CurrentFrameIndex);
    }

    [Fact]
    public void Tick_NonUniformDurations_Respected()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.5, 0.1));
        engine.Play();

        engine.Tick(TimeSpan.FromSeconds(0.2));
        Assert.Equal(0, engine.CurrentFrameIndex);
        engine.Tick(TimeSpan.FromSeconds(0.4));
        Assert.Equal(1, engine.CurrentFrameIndex);
    }

    [Fact]
    public void Tick_Loop_WrapsAround()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = true };
        engine.Load(MakeClip(0.1, 0.1));
        engine.Play();

        engine.Tick(TimeSpan.FromSeconds(0.25));
        Assert.Equal(0, engine.CurrentFrameIndex);
        Assert.True(engine.IsPlaying);
    }

    [Fact]
    public void Tick_NoLoop_StopsOnLastFrame()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.1, 0.1));
        engine.Play();

        engine.Tick(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.CurrentFrameIndex);
        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public void Speed_ScalesAdvancement()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false, Speed = 2.0 };
        engine.Load(MakeClip(0.2, 0.2));
        engine.Play();

        engine.Tick(TimeSpan.FromSeconds(0.1));
        Assert.Equal(1, engine.CurrentFrameIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Speed = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Speed = -1);
    }

    [Fact]
    public void StepNext_And_StepPrevious_WithLoop()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = true };
        engine.Load(MakeClip(0.1, 0.1, 0.1));

        engine.StepNext();
        engine.StepNext();
        Assert.Equal(2, engine.CurrentFrameIndex);
        engine.StepNext();
        Assert.Equal(0, engine.CurrentFrameIndex);
        engine.StepPrevious();
        Assert.Equal(2, engine.CurrentFrameIndex);
        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public void Steps_WithoutLoop_ClampAtEnds()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.1, 0.1));

        engine.StepPrevious();
        Assert.Equal(0, engine.CurrentFrameIndex);
        engine.StepNext();
        engine.StepNext();
        Assert.Equal(1, engine.CurrentFrameIndex);
    }

    [Fact]
    public void Scrub_MovesPlayhead_Proportionally()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.1, 0.1, 0.1, 0.1));

        engine.Scrub(0.0);
        Assert.Equal(0, engine.CurrentFrameIndex);
        engine.Scrub(0.5);
        Assert.Equal(2, engine.CurrentFrameIndex);
        engine.Scrub(1.0);
        Assert.Equal(3, engine.CurrentFrameIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SeekFrame(99));
    }

    [Fact]
    public void Operations_WithoutClip_Throw()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine();
        Assert.Equal(-1, engine.CurrentFrameIndex);
        Assert.Throws<InvalidOperationException>(() => engine.Play());
        Assert.Throws<InvalidOperationException>(() => engine.StepNext());
        engine.Pause(); // pausing with no clip is a no-op
        engine.Tick(TimeSpan.FromSeconds(1)); // ticking with no clip is a no-op
    }

    [Fact]
    public void Restart_ReturnsToFirstFrame()
    {
        var engine = new DcAnim.Core.Playback.PlaybackEngine { Loop = false };
        engine.Load(MakeClip(0.1, 0.1));
        engine.Play();
        engine.Tick(TimeSpan.FromSeconds(0.15));
        Assert.Equal(1, engine.CurrentFrameIndex);

        engine.Restart();
        Assert.Equal(0, engine.CurrentFrameIndex);
    }
}
