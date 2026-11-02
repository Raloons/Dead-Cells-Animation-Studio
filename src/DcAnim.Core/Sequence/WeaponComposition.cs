namespace DcAnim.Core.Sequence;

// Pixel operations are independent of any game runtime. Coordinates are in an
// unmirrored, untrimmed source-frame canvas. Masks are authored geometry, never
// inferred from clothing/weapon colors at render time (auto-detect only fills
// them during editing).
public readonly record struct PixelRun(int Y, int X, int Length);
public readonly record struct WeaponPose(double X, double Y, double Degrees,
    double ScaleX = 1, double ScaleY = 1, bool BehindBody = false);

public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid RGBA image geometry");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RgbaImage Copy() => new(Width, Height, (byte[])Pixels.Clone());
}

public static class WeaponComposition
{
    public static void ValidateMask(RgbaImage image, IEnumerable<PixelRun> mask)
    {
        foreach (var run in mask)
            if (run.Y < 0 || run.Y >= image.Height || run.X < 0 || run.Length <= 0 ||
                run.Length > image.Width || run.X > image.Width - run.Length)
                throw new ArgumentException("Weapon mask lies outside source canvas");
    }

    public static RgbaImage Remove(RgbaImage source, IReadOnlyList<PixelRun> mask)
    {
        ValidateMask(source, mask);
        var clean = source.Copy();
        foreach (var run in mask)
            Array.Clear(clean.Pixels, (run.Y * clean.Width + run.X) * 4, run.Length * 4);
        return clean;
    }

    public static void Restore(RgbaImage clean, RgbaImage donor, IReadOnlyList<PixelRun> mask)
    {
        if (clean.Width != donor.Width || clean.Height != donor.Height)
            throw new ArgumentException("Body correction canvas mismatch");
        ValidateMask(clean, mask);
        foreach (var run in mask)
            Array.Copy(donor.Pixels, (run.Y * donor.Width + run.X) * 4,
                clean.Pixels, (run.Y * clean.Width + run.X) * 4, run.Length * 4);
    }

    public static RgbaImage Compose(RgbaImage source, IReadOnlyList<PixelRun> remove,
        RgbaImage weapon, double pivotX, double pivotY, WeaponPose pose,
        IReadOnlyList<PixelRun>? foreground = null, RgbaImage? correctedBody = null)
    {
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Degrees) ||
            !double.IsFinite(pose.ScaleX) || !double.IsFinite(pose.ScaleY) ||
            pose.ScaleX == 0 || pose.ScaleY == 0 || !double.IsFinite(pivotX) || !double.IsFinite(pivotY))
            throw new ArgumentException("Invalid weapon transform");
        var body = Remove(source, remove);
        if (correctedBody != null)
        {
            if (correctedBody.Width != source.Width || correctedBody.Height != source.Height)
                throw new ArgumentException("Corrected body canvas mismatch");
            body = correctedBody.Copy();
        }
        if (foreground != null) ValidateMask(body, foreground);
        var output = pose.BehindBody
            ? new RgbaImage(body.Width, body.Height, new byte[body.Pixels.Length]) : body.Copy();
        double radians = pose.Degrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        for (int y = 0; y < output.Height; y++)
        for (int x = 0; x < output.Width; x++)
        {
            double dx = x + 0.5 - pose.X, dy = y + 0.5 - pose.Y;
            int sx = (int)Math.Floor((cos * dx + sin * dy) / pose.ScaleX + pivotX);
            int sy = (int)Math.Floor((-sin * dx + cos * dy) / pose.ScaleY + pivotY);
            if (sx >= 0 && sx < weapon.Width && sy >= 0 && sy < weapon.Height)
                Over(output.Pixels, (y * output.Width + x) * 4,
                    weapon.Pixels, (sy * weapon.Width + sx) * 4);
        }
        if (pose.BehindBody)
            for (int i = 0; i < output.Pixels.Length; i += 4) Over(output.Pixels, i, body.Pixels, i);
        else if (foreground != null)
            foreach (var run in foreground)
            for (int x = run.X; x < run.X + run.Length; x++)
            {
                int i = (run.Y * body.Width + x) * 4;
                Over(output.Pixels, i, body.Pixels, i);
            }
        return output;
    }

    public static RgbaImage Mirror(RgbaImage image)
    {
        var result = image.Copy();
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            Array.Copy(image.Pixels, (y * image.Width + x) * 4,
                result.Pixels, (y * image.Width + image.Width - 1 - x) * 4, 4);
        return result;
    }

    private static void Over(byte[] dest, int d, byte[] src, int s)
    {
        int a = src[s + 3];
        if (a == 0) return;
        if (a == 255) { Array.Copy(src, s, dest, d, 4); return; }
        int da = dest[d + 3];
        int alpha = a * 255 + da * (255 - a);
        for (int c = 0; c < 3; c++)
            dest[d + c] = (byte)((src[s + c] * a * 255 + dest[d + c] * da * (255 - a) + alpha / 2) / alpha);
        dest[d + 3] = (byte)((alpha + 127) / 255);
    }

    public static string MaskDigest(RgbaImage image, IReadOnlyList<PixelRun> region)
    {
        ValidateMask(image, region);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var run in region)
            hash.AppendData(image.Pixels, (run.Y * image.Width + run.X) * 4, run.Length * 4);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
