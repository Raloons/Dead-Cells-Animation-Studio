using DcAnim.Core.Atlas;
using DcAnim.Core.Pak;
using DcAnim.Core.Sources;
using SkiaSharp;

namespace DcAnim.Tests;

/// <summary>
/// Integration tests against genuine game resources. They never modify the
/// game: <c>res.pak</c> is opened read-only. They skip gracefully when no game
/// installation is present (e.g. CI), so the suite stays green everywhere.
/// Override paths with DCANIM_RESPAK / DCANIM_ATLASTOOL_REF.
/// </summary>
public sealed class NativeBeheadedTests
{
    private static string? ResPakPath =>
        Environment.GetEnvironmentVariable("DCANIM_RESPAK")
        ?? GameSourceLocator.FindResPak();

    private static string? AtlasToolRefDir => Environment.GetEnvironmentVariable("DCANIM_ATLASTOOL_REF");

    private static void SkipUnlessAvailable(out string resPak)
    {
        resPak = ResPakPath!;
        if (resPak is null || !File.Exists(resPak))
            Xunit.Assert.Skip("No Dead Cells res.pak found (set DCANIM_RESPAK to run native tests).");
    }

    [Fact]
    public void FromDirectory_LoadsSyntheticAtlas_PixelsMatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dcanim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // One 64x32 page: red 16x16 sprite at (8,8), composed onto a 32x32 canvas at (4,4).
            using (var page = new SKBitmap(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul))
            {
                using var canvas = new SKCanvas(page);
                canvas.Clear(SKColors.Transparent);
                canvas.DrawRect(new SKRect(8, 8, 24, 24), new SKPaint { Color = SKColors.Red });
                using var img = SKImage.FromBitmap(page);
                using var png = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(dir, "hero0.png"), png.ToArray());
            }
            // Minimal BATL: page 'hero0.png', region 'atk_00' + 'atk_01' (gap-free).
            var batl = new List<byte>();
            batl.AddRange("BATL"u8.ToArray());
            batl.Add(9); batl.AddRange("hero0.png"u8.ToArray());
            foreach (string name in new[] { "atk_00", "atk_01" })
            {
                batl.Add((byte)name.Length); batl.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
                batl.AddRange(new byte[] { 0, 0, 8, 0, 8, 0, 16, 0, 16, 0, 4, 0, 4, 0, 32, 0, 32, 0 });
            }
            File.WriteAllBytes(Path.Combine(dir, "hero.atlas"), batl.ToArray());

            using var loader = AnimationLoader.FromDirectory(dir, "hero.atlas");
            Assert.Equal(1, loader.Catalog.Count);
            var clip = loader.LoadClip("atk", fps: 10.0);

            Assert.Equal(2, clip.FrameCount);
            Assert.Equal(TimeSpan.FromSeconds(0.1), clip.Frames[0].Duration);
            var f = clip.Frames[0];
            Assert.Equal(32, f.Width);
            Assert.Equal(32, f.Height);
            // Pasted at (4,4): pixel (4,4) is red, pixel (0,0) is transparent.
            Assert.Equal((byte)255, f.Rgba[(4 * 32 + 4) * 4]);
            Assert.Equal((byte)0, f.Rgba[(4 * 32 + 4) * 4 + 1]);
            Assert.Equal((byte)0, f.Rgba[(4 * 32 + 4) * 4 + 2]);
            Assert.Equal((byte)255, f.Rgba[(4 * 32 + 4) * 4 + 3]);
            Assert.Equal((byte)0, f.Rgba[3]); // top-left transparent

            // Single-frame API must match the equivalent clip frame.
            var single = loader.LoadFrame("atk", 1, fps: 10.0);
            var whole = loader.LoadClip("atk", fps: 10.0);
            Assert.Equal(whole.Frames[1].Rgba, single.Rgba);
            Assert.Equal(whole.Frames[1].RegionName, single.RegionName);
            Assert.Throws<ArgumentOutOfRangeException>(() => loader.LoadFrame("atk", 99));
            Assert.Throws<KeyNotFoundException>(() => loader.LoadFrame("missing", 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BundledAtlas_IsCompleteAndParseable()
    {
        // Shipped default-outfit assets (used with permission; see README).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeadCells.AnimationStudio.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string atlasPath = Path.Combine(dir!.FullName, "bundled", "beheaded", "beheaded.atlas");
        Assert.True(File.Exists(atlasPath), "bundled atlas missing: " + atlasPath);

        var atlas = BatlParser.ParseFile(atlasPath);
        Assert.Equal(3, atlas.Pages.Count);
        Assert.Equal(10234, atlas.Regions.Count);
        Assert.Equal(0, atlas.DroppedTrailingRegions);
        foreach (var page in atlas.Pages)
            Assert.True(File.Exists(Path.Combine(dir.FullName, "bundled", "beheaded", page)), "missing page: " + page);

        using var loader = AnimationLoader.FromDirectory(Path.Combine(dir.FullName, "bundled", "beheaded"));
        var clip = loader.LoadClip("AtkKatanaB2");
        Assert.Equal(11, clip.FrameCount);
        Assert.All(clip.Frames, f => Assert.True(f.Rgba.Any(b => b != 0), "blank bundled frame"));
    }

    [Fact]
    public void ResPak_ContainsBeheadedAtlas()
    {
        SkipUnlessAvailable(out var resPak);
        using var pak = PakReader.Open(resPak);

        Assert.True(pak.Contains("atlas/beheaded.atlas"), "atlas/beheaded.atlas missing from res.pak");
        Assert.True(pak.Contains("atlas/beheaded_tracks.json"), "atlas/beheaded_tracks.json missing from res.pak");
        Assert.True(pak.Contains("atlas/beheaded0.png"), "atlas/beheaded0.png missing from res.pak");
    }

    [Fact]
    public void BatlParser_DecodesRealBeheadedAtlas()
    {
        SkipUnlessAvailable(out var resPak);
        using var pak = PakReader.Open(resPak);
        var atlas = BatlParser.Parse(pak.ReadFile("atlas/beheaded.atlas"));

        Assert.Equal(3, atlas.Pages.Count);
        Assert.Equal("beheaded0.png", atlas.Pages[0]);
        Assert.Equal(10234, atlas.Regions.Count);
        Assert.Equal(0, atlas.DroppedTrailingRegions);
        Assert.Contains(atlas.Regions, r => r.Name == "scissorsAtkC_04");
    }

    [Fact]
    public void ResPak_CompleteDirectoryAndPayloadTails_ValidateAgainstNativeChecksums()
    {
        SkipUnlessAvailable(out var resPak);
        using var pak = PakReader.Open(resPak);
        Assert.Equal(238570, pak.DataStart);
        Assert.Equal(7055, pak.Entries.Count);
        Assert.True(pak.Contains("data.cdb"));
        using var tracks = System.Text.Json.JsonDocument.Parse(pak.ReadFile("atlas/beheaded_tracks.json"));
        Assert.Equal(572, tracks.RootElement.EnumerateObject().Count());
        var png = pak.ReadFile("atlas/beheaded0.png");
        Assert.Equal(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }, png[^12..]);
    }

    [Fact]
    public void Catalog_ContainsKatanaFamily_WithExpectedFrameCounts()
    {
        SkipUnlessAvailable(out var resPak);
        using var loader = AnimationLoader.FromResPak(resPak);

        var expected = new Dictionary<string, int>
        {
            ["AtkKatanaA"] = 26,
            ["AtkKatanaB"] = 16,
            ["AtkKatanaB2"] = 11,
            ["AtkKatanaB3"] = 11,
            ["AtkKatanaB4"] = 16,
            ["LoadAtkKatanaA"] = 13,
            ["LoadAtkKatanaB"] = 16,
        };
        foreach (var (id, frames) in expected)
        {
            Assert.True(loader.Catalog.Contains(id), $"Animation '{id}' missing from native catalog.");
            Assert.Equal(frames, loader.Catalog.GetFrameRegions(id).Count);
            Assert.Empty(loader.Catalog.GetNumberingGaps(id));
        }
    }

    [Fact]
    public void Loader_ComposesGenuineKatanaFrames_NonBlankAndDistinct()
    {
        SkipUnlessAvailable(out var resPak);
        using var loader = AnimationLoader.FromResPak(resPak);
        var clip = loader.LoadClip("AtkKatanaB2");

        Assert.Equal(11, clip.FrameCount);
        foreach (var f in clip.Frames)
        {
            Assert.Equal(150, f.Width);
            Assert.Equal(150, f.Height);
            Assert.True(f.Rgba.Any(b => b != 0), $"Frame {f.Index} is fully blank.");
        }
        // Consecutive frames must differ (real animation, not a static image).
        for (int i = 1; i < clip.FrameCount; i++)
            Assert.NotEqual(clip.Frames[i - 1].Rgba, clip.Frames[i].Rgba);
    }

    [Fact]
    public void Composer_MatchesOfficialAtlasToolOutput_PixelExact()
    {
        SkipUnlessAvailable(out var resPak);
        if (AtlasToolRefDir is null || !Directory.Exists(AtlasToolRefDir))
            Xunit.Assert.Skip("Set DCANIM_ATLASTOOL_REF to an AtlasTool -Expand dir for pixel comparison.");

        using var loader = AnimationLoader.FromResPak(resPak);
        var clip = loader.LoadClip("AtkKatanaB2");

        foreach (var frame in clip.Frames)
        {
            string pattern = Path.Combine(AtlasToolRefDir, $"{frame.RegionName}-=-*.png");
            var matches = Directory.GetFiles(Path.GetDirectoryName(pattern)!, Path.GetFileName(pattern));
            Assert.Single(matches);
            using var refBmp = SkiaSharp.SKBitmap.Decode(matches[0]);
            Assert.NotNull(refBmp);
            Assert.Equal(refBmp.Width, frame.Width);
            Assert.Equal(refBmp.Height, frame.Height);
            var expected = new byte[frame.Width * frame.Height * 4];
            int k = 0;
            for (int y = 0; y < refBmp.Height; y++)
                for (int x = 0; x < refBmp.Width; x++)
                {
                    var c = refBmp.GetPixel(x, y);
                    expected[k++] = c.Red;
                    expected[k++] = c.Green;
                    expected[k++] = c.Blue;
                    expected[k++] = c.Alpha;
                }
            Assert.Equal(expected, frame.Rgba);
        }
    }
}
