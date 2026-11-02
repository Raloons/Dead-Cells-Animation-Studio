using DcAnim.Core.Sequence;

namespace DcAnim.Tests;

public sealed class WeaponCompositionTests
{
    private static RgbaImage Canvas(int w, int h) => new(w, h, new byte[w * h * 4]);
    private static void Pixel(RgbaImage img, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        int i = (y * img.Width + x) * 4;
        img.Pixels[i] = r; img.Pixels[i + 1] = g; img.Pixels[i + 2] = b; img.Pixels[i + 3] = a;
    }
    private static byte[] At(RgbaImage img, int x, int y) =>
        img.Pixels[((y * img.Width + x) * 4)..((y * img.Width + x + 1) * 4)];

    [Fact]
    public void Remove_ErasesOnlyMask()
    {
        var src = Canvas(12, 12);
        Pixel(src, 2, 2, 11, 22, 33);
        Pixel(src, 8, 4, 201, 92, 3);
        Pixel(src, 7, 4, 61, 31, 11);
        var clean = WeaponComposition.Remove(src, new[] { new PixelRun(4, 8, 1) });
        Assert.True(At(clean, 8, 4).All(c => c == 0));
        Assert.Equal(At(src, 7, 4), At(clean, 7, 4));
        Assert.Equal(255, At(src, 8, 4)[3]); // source immutable
    }

    [Fact]
    public void Compose_PlacesWeaponAndRespectsForeground()
    {
        var src = Canvas(12, 12);
        Pixel(src, 8, 4, 201, 92, 3);
        Pixel(src, 7, 4, 61, 31, 11);
        var mask = new[] { new PixelRun(4, 8, 1) };
        var blade = Canvas(3, 1);
        for (int x = 0; x < 3; x++) Pixel(blade, x, 0, 101, 102, 103);
        var result = WeaponComposition.Compose(src, mask, blade, 0, 0,
            new WeaponPose(7, 4, 0), new[] { new PixelRun(4, 7, 1) });
        Assert.Equal(new byte[] { 101, 102, 103, 255 }, At(result, 8, 4));
        Assert.Equal(At(src, 7, 4), At(result, 7, 4));
    }

    [Fact]
    public void Compose_BehindBodyDrawsUnderBody()
    {
        var src = Canvas(12, 12);
        Pixel(src, 8, 4, 201, 92, 3);
        Pixel(src, 7, 4, 61, 31, 11);
        var blade = Canvas(3, 1);
        for (int x = 0; x < 3; x++) Pixel(blade, x, 0, 101, 102, 103);
        var result = WeaponComposition.Compose(src, new[] { new PixelRun(4, 8, 1) }, blade, 0, 0,
            new WeaponPose(7, 4, 0, BehindBody: true));
        Assert.Equal(At(src, 7, 4), At(result, 7, 4));
        Assert.Equal((byte)101, At(result, 8, 4)[0]);
    }

    [Fact]
    public void Compose_RotatesAroundPivot()
    {
        var blade = Canvas(3, 1);
        for (int x = 0; x < 3; x++) Pixel(blade, x, 0, 101, 102, 103);
        var result = WeaponComposition.Compose(Canvas(12, 12), Array.Empty<PixelRun>(), blade, 0, 0,
            new WeaponPose(7, 4, 90));
        Assert.NotEqual(0, At(result, 6, 4)[3]);
        Assert.NotEqual(0, At(result, 6, 6)[3]);
        Assert.Equal(0, At(result, 8, 4)[3]);
    }

    [Fact]
    public void Mirror_IsInvolutive()
    {
        var src = Canvas(4, 2);
        Pixel(src, 0, 0, 1, 2, 3);
        var twice = WeaponComposition.Mirror(WeaponComposition.Mirror(src));
        Assert.Equal(src.Pixels, twice.Pixels);
    }

    [Fact]
    public void Validate_RejectsBadGeometryAndTransforms()
    {
        var src = Canvas(4, 4);
        Assert.Throws<ArgumentException>(() => WeaponComposition.Remove(src, new[] { new PixelRun(0, 3, 2) }));
        var blade = Canvas(1, 1);
        Assert.Throws<ArgumentException>(() => WeaponComposition.Compose(src, Array.Empty<PixelRun>(), blade, 0, 0,
            new WeaponPose(0, 0, double.NaN)));
        Assert.Throws<ArgumentException>(() => WeaponComposition.Compose(src, Array.Empty<PixelRun>(), blade, 0, 0,
            new WeaponPose(0, 0, 0, ScaleX: 0)));
    }

    [Fact]
    public void Palette_ExtractsBandsAndEstimatesAxis()
    {
        var img = Canvas(16, 4);
        for (int x = 2; x < 12; x++) Pixel(img, x, 1, 175, 32, 0); // shaft-like row
        Pixel(img, 12, 1, 182, 32, 0); // head
        var runs = WeaponPalette.ExtractRuns(img.Pixels, 16, 4, 32);
        Assert.NotEmpty(runs);
        var points = WeaponPalette.ToPoints(runs);
        var (deg, _, _, count) = WeaponPalette.ShaftAxis(points);
        Assert.True(count >= 10);
        Assert.InRange(Math.Abs(deg), 0, 10); // horizontal shaft
    }

    [Fact]
    public void AutoDetect_FillsExplicitRunsAndPoses()
    {
        var bodies = new List<(int, RgbaImage)>();
        for (int f = 0; f < 3; f++)
        {
            var img = Canvas(16, 16);
            for (int x = 2; x < 10; x++) Pixel(img, x, 4 + f, 175, 32, 0);
            Pixel(img, 1, 4 + f, 200, 96, 100); // body pixel
            bodies.Add((f, img));
        }
        var donor = Canvas(16, 16);
        for (int x = 1; x < 6; x++) Pixel(donor, x, 2, 150, 159, 10);
        var weapon = WeaponAutoDetect.Build("AtkKatanaB2", 0, 32, 159, bodies, donor);
        Assert.Equal("AtkKatanaB2", weapon.DonorAnimationId);
        Assert.Equal(3, weapon.Frames.Count);
        Assert.All(weapon.Frames, f => Assert.NotEmpty(f.Remove));
        Assert.NotEmpty(weapon.DonorRegion);
    }

    [Fact]
    public void AutoDetect_NonBodyModeTreatsNonBodyColorAsWeapon()
    {
        var bodies = new List<(int, RgbaImage)>();
        var img = Canvas(16, 16);
        for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) Pixel(img, x, y, 200, 96, 100);
        for (int x = 2; x < 10; x++) Pixel(img, x, 4, 60, 200, 10); // green shaft, G != 96
        Pixel(img, 10, 4, 250, 180, 20); // yellow head, G != 96
        bodies.Add((0, img));
        var donor = Canvas(16, 16);
        for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) Pixel(donor, x, y, 200, 96, 100);
        for (int x = 1; x < 6; x++) Pixel(donor, x, 2, 150, 159, 10);
        var weapon = WeaponAutoDetect.Build("Donor", 0, 32, 159, bodies, donor,
            nonBodyMode: true, bodyColorGreen: 96);
        Assert.True(weapon.NonBodyMode);
        var frame = Assert.Single(weapon.Frames);
        Assert.True(frame.Remove.Sum(r => r.Length) >= 9); // shaft + head, not just one band
    }

    [Fact]
    public void Serializer_RoundTripsWeaponV4()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wseq-{Guid.NewGuid():N}.dcanim");
        try
        {
            var doc = new SequenceDocument();
            doc.New("Weapon Test");
            var c = doc.AddClip("AtkGoldDiggerA", 1.0);
            doc.SetClipWeapon(c.Id, new WeaponClipOverride
            {
                DonorAnimationId = "AtkKatanaB2",
                DonorFrame = 0,
                DonorRegion = new List<PixelRun> { new(2, 1, 5) },
                DonorPivotX = 2.5,
                DonorPivotY = 2.5,
                BodyMaskGreen = 32,
                DonorMaskGreen = 159,
                Frames = new List<WeaponFrameOverride>
                {
                    new() { SourceFrame = 0, Remove = new List<PixelRun> { new(4, 2, 8) }, Pose = new WeaponPose(5, 5, 12) },
                },
            });
            ProjectSerializer.Save(doc.Project, path);
            var loaded = ProjectSerializer.Load(path);
            var w = Assert.Single(loaded.Clips).Weapon;
            Assert.NotNull(w);
            Assert.Equal("AtkKatanaB2", w!.DonorAnimationId);
            Assert.Equal(new PixelRun(4, 2, 8), Assert.Single(w.Frames).Remove[0]);
            Assert.Equal(12, Assert.Single(w.Frames).Pose.Degrees);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Serializer_OldVersionsLoadWithoutWeapon()
    {
        string path = Path.Combine(Path.GetTempPath(), $"oldseq-{Guid.NewGuid():N}.dcanim");
        try
        {
            File.WriteAllText(path, """{"format":"dcanim","version":1,"name":"Old","fps":30,"clips":[{"id":"c1","animation":"AtkKatanaB2","speed":1.0,"keyframes":[]}],"effects":[],"audio":[],"canvasWidth":0,"canvasHeight":0,"rippleFxAudio":true}""");
            var loaded = ProjectSerializer.Load(path);
            Assert.Null(Assert.Single(loaded.Clips).Weapon);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void PlanIO_RoundTripsExplicitRunsAndPoses()
    {
        var bodies = new List<(int, RgbaImage)>();
        var body = Canvas(16, 16);
        for (int x = 2; x < 10; x++) Pixel(body, x, 4, 175, 32, 0);
        Pixel(body, 1, 4, 200, 96, 100);
        bodies.Add((0, body));
        var donor = Canvas(16, 16);
        for (int x = 1; x < 6; x++) Pixel(donor, x, 2, 150, 159, 10);
        var weapon = WeaponAutoDetect.Build("AtkKatanaB2", 0, 32, 159, bodies, donor);
        RgbaImage Resolve(string anim, int frame) => anim == "AtkGoldDiggerA" ? body : donor;
        string json = WeaponPlanIO.Export("test-plan", "AtkGoldDiggerA", 16, 16, weapon, Resolve);
        var (bodyAnim, imported, w, h, id) = WeaponPlanIO.Import(json);
        Assert.Equal("AtkGoldDiggerA", bodyAnim);
        Assert.Equal("AtkKatanaB2", imported.DonorAnimationId);
        Assert.Equal(16, w);
        Assert.Equal("test-plan", id);
        Assert.Equal(weapon.Frames[0].Pose.X, imported.Frames[0].Pose.X, 9);
    }

    [Fact]
    public void Compositor_AppliesWeaponOverrideToRender()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wcomp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var page = new SkiaSharp.SKBitmap(64, 32, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            for (int x = 2; x < 10; x++) page.SetPixel(x, 4, new SkiaSharp.SKColor(175, 32, 0, 255));
            page.SetPixel(1, 4, new SkiaSharp.SKColor(200, 96, 100, 255));
            for (int x = 33; x < 38; x++) page.SetPixel(x, 2, new SkiaSharp.SKColor(150, 159, 10, 255));
            using (var img = SkiaSharp.SKImage.FromBitmap(page))
            using (var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(dir, "p.png"), png.ToArray());
            var batl = new List<byte>();
            batl.AddRange("BATL"u8.ToArray());
            var pn = "p.png"u8.ToArray();
            batl.Add((byte)pn.Length); batl.AddRange(pn);
            void Region(string name, int x, int y)
            {
                batl.Add((byte)name.Length);
                batl.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
                foreach (int v in new[] { 0, x, y, 16, 16, 0, 0, 16, 16 })
                    batl.AddRange(BitConverter.GetBytes((ushort)v));
            }
            Region("body_00", 0, 0);
            Region("donor_00", 32, 0);
            File.WriteAllBytes(Path.Combine(dir, "w.atlas"), batl.ToArray());
            using var loader = DcAnim.Core.Sources.AnimationLoader.FromDirectory(dir, "w.atlas");
            var native = new NativeFrameCache();
            var fx = new EffectImageCache();
            var bodyF = loader.LoadFrame("body", 0);
            var donorF = loader.LoadFrame("donor", 0);
            var bodies = new List<(int, RgbaImage)> { (0, new RgbaImage(bodyF.Width, bodyF.Height, (byte[])bodyF.Rgba.Clone())) };
            var donorImg = new RgbaImage(donorF.Width, donorF.Height, (byte[])donorF.Rgba.Clone());
            var weapon = WeaponAutoDetect.Build("donor", 0, 32, 159, bodies, donorImg);
            var project = new SequenceProject { Name = "W", Fps = 30 };
            project.Clips.Add(new SequenceClip { Id = "c1", AnimationId = "body", Weapon = weapon });
            var rendered = SequenceCompositor.Render(project, loader, native, fx, out _);
            Assert.Single(rendered.Frames);
            var plain = new SequenceProject { Name = "P", Fps = 30 };
            plain.Clips.Add(new SequenceClip { Id = "c1", AnimationId = "body" });
            var plainRendered = SequenceCompositor.Render(plain, loader, new NativeFrameCache(), fx, out _);
            Assert.NotEqual(plainRendered.Frames[0], rendered.Frames[0]);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
