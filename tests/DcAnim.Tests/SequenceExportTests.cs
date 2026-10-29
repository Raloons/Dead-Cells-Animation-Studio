using System.Text;
using DcAnim.Core.Atlas;
using DcAnim.Core.Export;
using DcAnim.Core.Model;
using DcAnim.Core.Sequence;
using DcAnim.Core.Sources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DcAnim.Tests;

/// <summary>Sequence composition + exporter tests on a synthetic atlas
/// (no game files needed). Native-asset round trips are covered by the
/// evidence workflow in docs/AS-M3-REPORT.md.</summary>
public sealed class SequenceExportTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"seqfx-{Guid.NewGuid():N}");
        public AnimationLoader Loader { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            // Page 64x64: red square at top-left, green square at bottom-right.
            using var page = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var canvas = new SkiaSharp.SKCanvas(page))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.DrawRect(new SkiaSharp.SKRect(0, 0, 32, 32),
                    new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Red });
                canvas.DrawRect(new SkiaSharp.SKRect(32, 32, 64, 64),
                    new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 255, 0, 255) });
            }
            using (var img = SkiaSharp.SKImage.FromBitmap(page))
            using (var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(Dir, "s0.png"), png.ToArray());

            //Regions: atk_00..02 (16x16 red at (0,0) on 32x32 canvas),
            //         zap_00..01 (16x16 green at (32,32) on 32x32 canvas).
            var batl = new List<byte>();
            batl.AddRange("BATL"u8.ToArray());
            var pageName = "s0.png"u8.ToArray();
            batl.Add((byte)pageName.Length);
            batl.AddRange(pageName);
            void Region(string name, int x, int y)
            {
                batl.Add((byte)name.Length);
                batl.AddRange(Encoding.UTF8.GetBytes(name));
                foreach (int v in new[] { 0, x, y, 16, 16, 0, 0, 32, 32 })
                    batl.AddRange(BitConverter.GetBytes((ushort)v));
            }
            Region("atk_00", 0, 0); Region("atk_01", 0, 0); Region("atk_02", 0, 0);
            Region("zap_00", 32, 32); Region("zap_01", 32, 32);
            File.WriteAllBytes(Path.Combine(Dir, "s.atlas"), batl.ToArray());
            Loader = AnimationLoader.FromDirectory(Dir, "s.atlas");
        }

        public void Dispose()
        {
            Loader.Dispose();
            Directory.Delete(Dir, recursive: true);
        }
    }

    private static SequenceProject KatanaLike() =>
        // Mirrors the evidence sequence shape: 3 + 2 source frames.
        new SequenceProject
        {
            Name = "T",
            Fps = 30,
            Clips =
            {
                new SequenceClip { Id = "c1", AnimationId = "atk", Speed = 1.0 },
                new SequenceClip { Id = "c2", AnimationId = "zap", Speed = 0.5 },
            },
        };

    [Fact]
    public void Composer_BuildsOrderedFramesWithExactBoundaries()
    {
        using var fx = new Fixture();
        var (clip, resolved) = SequenceComposer.Compose(KatanaLike(), fx.Loader);

        // atk: 3 src @1x -> 3 out; zap: 2 src @0.5x -> 4 out. Total 7.
        Assert.Equal(7, clip.FrameCount);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(new[] { 0, 3 }, resolved.Select(r => r.StartFrame).ToArray());
        Assert.Equal(new[] { 3, 4 }, resolved.Select(r => r.OutputFrames).ToArray());

        // Clip 1 identity, clip 2 holds each source twice.
        Assert.Equal("atk_00", clip.Frames[0].RegionName);
        Assert.Equal("atk_02", clip.Frames[2].RegionName);
        Assert.Equal("zap_00", clip.Frames[3].RegionName);
        Assert.Equal("zap_00", clip.Frames[4].RegionName);
        Assert.Equal("zap_01", clip.Frames[5].RegionName);
        Assert.Equal("zap_01", clip.Frames[6].RegionName);

        // Durations uniform at project fps.
        Assert.All(clip.Frames, f => Assert.Equal(TimeSpan.FromSeconds(1.0 / 30), f.Duration));

        var (w, h) = SequenceComposer.CanvasSize(clip.Frames);
        Assert.Equal((32, 32), (w, h));
    }

    [Fact]
    public void Composer_SkipsMissingAnimationsButKeepsRest()
    {
        using var fx = new Fixture();
        var project = new SequenceProject { Name = "T", Fps = 30 };
        project.Clips.Add(new SequenceClip { Id = "a", AnimationId = "atk", Speed = 1 });
        project.Clips.Add(new SequenceClip { Id = "m", AnimationId = "Missing_Id", Speed = 1 });
        project.Clips.Add(new SequenceClip { Id = "z", AnimationId = "zap", Speed = 1 });

        var (clip, resolved) = SequenceComposer.Compose(project, fx.Loader);
        Assert.Equal(5, clip.FrameCount); // 3 + 2, missing skipped
        Assert.Equal(2, resolved.Count);
        var validation = ProjectSerializer.ValidateAnimations(project, id => fx.Loader.Catalog.Contains(id));
        Assert.Equal(new[] { "Missing_Id" }, validation.MissingAnimationIds);
    }

    [Fact]
    public void PngExport_WritesDeterministicTransparentFrames()
    {
        using var fx = new Fixture();
        var (clip, _) = SequenceComposer.Compose(KatanaLike(), fx.Loader);
        string outDir = Path.Combine(Path.GetTempPath(), $"png-{Guid.NewGuid():N}");

        try
        {
            var paths = PngSequenceExporter.Export(clip.Frames, outDir).ToList();
            Assert.Equal(7, paths.Count);
            Assert.Equal("frame_0000.png", Path.GetFileName(paths[0]));
            Assert.Equal("frame_0006.png", Path.GetFileName(paths[6]));

            // Pixel check: frame 0 is the red square centered on 32x32.
            using var bmp = SkiaSharp.SKBitmap.Decode(paths[0]);
            Assert.Equal(32, bmp.Width);
            Assert.Equal(32, bmp.Height);
            var red = bmp.GetPixel(2, 2);
            Assert.Equal(255, red.Red);
            Assert.Equal(0, red.Green);
            // Corner is transparent.
            Assert.Equal(0, bmp.GetPixel(31, 31).Alpha);

            // Preview/export consistency: PNG frame == preview frame, centered.
            var preview = clip.Frames[0];
            var canvas = ExportCanvas.ComposeCentered(preview, 32, 32);
            using var expect = ExportCanvas.ToSkBitmap(canvas, 32, 32);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    Assert.Equal(expect.GetPixel(x, y), bmp.GetPixel(x, y));

            // Refuses to overwrite silently.
            Assert.Throws<IOException>(() => PngSequenceExporter.Export(clip.Frames, outDir));
            // ...unless asked to.
            PngSequenceExporter.Export(clip.Frames, outDir, overwrite: true);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void GifExport_RoundTripsFramesTimingAndTransparency()
    {
        using var fx = new Fixture();
        var (clip, _) = SequenceComposer.Compose(KatanaLike(), fx.Loader);
        string path = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid():N}.gif");

        try
        {
            var info = GifExporter.Export(clip.Frames, 30, path, loop: true, overwrite: false);
            Assert.Equal(7, info.FrameCount);
            Assert.Equal(3, info.FrameDelayCentiseconds); // round(1000/30/10)
            Assert.Equal(100.0 / 3, info.EffectiveFps, precision: 9);
            Assert.True(info.Loop);

            using var gif = Image.Load<Rgba32>(path);
            Assert.Equal(7, gif.Frames.Count);
            foreach (var f in gif.Frames)
                Assert.Equal(3, f.Metadata.GetGifMetadata().FrameDelay);
            Assert.Equal((ushort)0, gif.Metadata.GetGifMetadata().RepeatCount);
            // Transparency survives (frame corner).
            Assert.Equal(0, gif.Frames[0][31, 31].A);
            // Red square survives quantization (flat area, no dither).
            var px = gif.Frames[0][2, 2];
            Assert.True(px.R > 200 && px.G < 60 && px.B < 60);

            Assert.Throws<IOException>(() => GifExporter.Export(clip.Frames, 30, path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SheetExport_CoordinatesAndMetadataMatch()
    {
        using var fx = new Fixture();
        var (clip, resolved) = SequenceComposer.Compose(KatanaLike(), fx.Loader);
        string dir = Path.Combine(Path.GetTempPath(), $"sheet-{Guid.NewGuid():N}");
        string img = Path.Combine(dir, "sheet.png");
        string meta = Path.Combine(dir, "sheet.json");

        try
        {
            var info = SpriteSheetExporter.Export(clip.Frames, resolved, 30, img, meta,
                columns: 4, padding: 2);

            // 7 frames, 4 cols -> 2 rows; cells 32 + 2px padding.
            Assert.Equal(4, info.Columns);
            Assert.Equal(2, info.Rows);
            Assert.Equal(4 * 32 + 5 * 2, info.ImageWidth);
            Assert.Equal(2 * 32 + 3 * 2, info.ImageHeight);
            Assert.Equal(7, info.Frames.Count);

            var f0 = info.Frames[0];
            Assert.Equal((0, 2, 2, 32, 32), (f0.Index, f0.X, f0.Y, f0.Width, f0.Height));
            Assert.Equal("atk", f0.AnimationId);
            var f4 = info.Frames[4]; // second zap output frame -> source frame 0
            Assert.Equal("zap", f4.AnimationId);
            Assert.Equal(0, f4.SourceFrame);
            Assert.Equal(1, f4.ClipIndex);

            Assert.Equal(2, info.Clips.Count);
            Assert.Equal((0, 3), (info.Clips[0].StartFrame, info.Clips[0].FrameCount));
            Assert.Equal((3, 4), (info.Clips[1].StartFrame, info.Clips[1].FrameCount));

            // Metadata file parses and agrees.
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(meta));
            Assert.Equal(7, doc.RootElement.GetProperty("frames").GetArrayLength());

            // Sheet pixels: red square present in cell 0.
            using var sheet = SkiaSharp.SKBitmap.Decode(img);
            Assert.Equal(info.ImageWidth, sheet.Width);
            var red = sheet.GetPixel(2 + 2, 2 + 2);
            Assert.True(red.Red > 200 && red.Alpha == 255);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
