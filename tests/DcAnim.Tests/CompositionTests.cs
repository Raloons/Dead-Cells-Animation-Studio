using System.Text;
using DcAnim.Core.Export;
using DcAnim.Core.Sequence;
using DcAnim.Core.Sources;

namespace DcAnim.Tests;

/// <summary>Compositor + rendered-export parity tests on synthetic content.</summary>
public sealed class CompositionTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"cmp-{Guid.NewGuid():N}");
        public AnimationLoader Loader { get; }
        public NativeFrameCache Native { get; } = new();
        public EffectImageCache Effects { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            using var page = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var canvas = new SkiaSharp.SKCanvas(page))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.DrawRect(new SkiaSharp.SKRect(0, 0, 32, 32),
                    new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Red });
            }
            using (var img = SkiaSharp.SKImage.FromBitmap(page))
            using (var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(Dir, "s0.png"), png.ToArray());

            var batl = new List<byte>();
            batl.AddRange("BATL"u8.ToArray());
            var pn = "s0.png"u8.ToArray();
            batl.Add((byte)pn.Length);
            batl.AddRange(pn);
            void Region(string name, int x, int y)
            {
                batl.Add((byte)name.Length);
                batl.AddRange(Encoding.UTF8.GetBytes(name));
                foreach (int v in new[] { 0, x, y, 16, 16, 0, 0, 32, 32 })
                    batl.AddRange(BitConverter.GetBytes((ushort)v));
            }
            Region("idle_00", 0, 0);
            Region("idle_01", 0, 0);
            File.WriteAllBytes(Path.Combine(Dir, "s.atlas"), batl.ToArray());
            Loader = AnimationLoader.FromDirectory(Dir, "s.atlas");
        }

        public string WriteEffect(string name, byte r, byte g, byte b, byte a = 255)
        {
            using var bmp = new SkiaSharp.SKBitmap(8, 8, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var canvas = new SkiaSharp.SKCanvas(bmp))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.DrawRect(new SkiaSharp.SKRect(0, 0, 8, 8),
                    new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(r, g, b, a) });
            }
            string path = Path.Combine(Dir, name);
            using (var img = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(path, png.ToArray());
            return path;
        }

        public void Dispose()
        {
            Loader.Dispose();
            Effects.Dispose();
            Directory.Delete(Dir, recursive: true);
        }
    }

    private static SequenceProject OneClip(string anim = "idle") =>
        new() { Name = "T", Fps = 30, Clips = { new SequenceClip { Id = "c1", AnimationId = anim, Speed = 1 } } };

    [Fact]
    public void Compose_IdentityReproducesSourcePixels()
    {
        using var fx = new Fixture();
        var rendered = SequenceCompositor.Render(OneClip(), fx.Loader, fx.Native, fx.Effects,
            out var resolved);
        Assert.Equal((32, 32), (rendered.Width, rendered.Height));
        Assert.Equal(2, rendered.Frames.Count);
        Assert.Single(resolved);

        // Frame 0 == native frame 0 centered (canvas equals native size).
        var native = fx.Loader.LoadFrame("idle", 0);
        Assert.Equal(native.Rgba, rendered.Frames[0]);
    }

    [Fact]
    public void Compose_TranslationMovesPixels()
    {
        using var fx = new Fixture();
        var project = OneClip();
        project.Clips[0].BaseTransform = Transform.Identity with { X = 8, Y = 4 };
        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        // Red square was at (0..15, 0..15); shifted by (8,4) -> (8..23, 4..19).
        var f = rendered.Frames[0];
        Assert.True(IsRed(f, 32, 10, 6));
        Assert.Equal(0, f[(6 * 32 + 2) * 4 + 3]); // old spot now transparent
    }

    [Fact]
    public void Compose_OpacityScalesAlpha()
    {
        using var fx = new Fixture();
        var project = OneClip();
        project.Clips[0].BaseTransform = Transform.Identity with { Opacity = 0.5 };
        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        byte a = rendered.Frames[0][(2 * 32 + 2) * 4 + 3];
        Assert.InRange(a, 120, 135); // 255 * 0.5, rounding-tolerant
    }

    [Fact]
    public void Compose_EffectOverlaysInLayerOrder()
    {
        using var fx = new Fixture();
        string blue = fx.WriteEffect("blue.png", 0, 0, 255);
        string green = fx.WriteEffect("green.png", 0, 255, 0);
        var project = OneClip();
        project.Effects.Add(new EffectClip { Id = "e1", ImagePath = blue, StartFrame = 0, DurationFrames = 2, Layer = 0 });
        project.Effects.Add(new EffectClip { Id = "e2", ImagePath = green, StartFrame = 0, DurationFrames = 2, Layer = 1 });

        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        // Effect images default pivot center placed at canvas center: 8x8 at ((32-8)/2, (32-8)/2) = (12,12).
        var f = rendered.Frames[0];
        int i = (14 * 32 + 14) * 4;
        Assert.True(f[i + 1] > 200 && f[i] < 60 && f[i + 2] < 60); // green wins (layer 1)

        // Swap layers -> blue wins.
        project.Effects[0].Layer = 5;
        rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        f = rendered.Frames[0];
        i = (14 * 32 + 14) * 4;
        Assert.True(f[i + 2] > 200 && f[i] < 60 && f[i + 1] < 60);
    }

    [Fact]
    public void Compose_MissingEffectIsSkipped()
    {
        using var fx = new Fixture();
        var project = OneClip();
        project.Effects.Add(new EffectClip
        {
            Id = "e9", ImagePath = Path.Combine(fx.Dir, "nope.png"),
            StartFrame = 0, DurationFrames = 2,
        });
        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        Assert.Equal(2, rendered.Frames.Count); // no crash; clip intact
    }

    [Fact]
    public void Compose_KeyframeDrivenMotion()
    {
        using var fx = new Fixture();
        var project = OneClip();
        project.Clips[0].Keyframes.Add(new TransformKeyframe(TransformProperty.X, 0, 0));
        project.Clips[0].Keyframes.Add(new TransformKeyframe(TransformProperty.X, 1, 16));
        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        Assert.Equal(2, rendered.Frames.Count);
        Assert.True(IsRed(rendered.Frames[0], 32, 2, 2));
        Assert.True(IsRed(rendered.Frames[1], 32, 18, 2));
    }

    [Fact]
    public void RenderedExport_PngMatchesComposedBytes()
    {
        using var fx = new Fixture();
        var project = OneClip();
        project.Clips[0].BaseTransform = Transform.Identity with { X = 4 };
        var rendered = SequenceCompositor.Render(project, fx.Loader, fx.Native, fx.Effects, out _);
        string dir = Path.Combine(Path.GetTempPath(), $"rexp-{Guid.NewGuid():N}");
        try
        {
            var paths = PngSequenceExporter.ExportRendered(rendered, dir, overwrite: true).ToList();
            Assert.Equal(2, paths.Count);
            using var bmp = SkiaSharp.SKBitmap.Decode(paths[0]);
            Assert.Equal(rendered.Width, bmp.Width);
            for (int y = 0; y < rendered.Height; y++)
                for (int x = 0; x < rendered.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    int i = (y * rendered.Width + x) * 4;
                    var want = rendered.Frames[0];
                    Assert.Equal(want[i], c.Red);
                    Assert.Equal(want[i + 1], c.Green);
                    Assert.Equal(want[i + 2], c.Blue);
                    Assert.Equal(want[i + 3], c.Alpha);
                }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool IsRed(byte[] rgba, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return rgba[i] > 200 && rgba[i + 1] < 60 && rgba[i + 2] < 60 && rgba[i + 3] > 200;
    }
}
