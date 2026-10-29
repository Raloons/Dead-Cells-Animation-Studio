using DcAnim.Core.Model;
using SkiaSharp;

namespace DcAnim.Core.Atlas;

/// <summary>
/// Composes animation frames from a parsed BATL atlas plus its page PNGs.
/// Each region is cropped from its page and pasted top-left at (OffsetX, OffsetY)
/// onto a transparent canvas of (OriginalWidth, OriginalHeight) — semantics
/// verified pixel-exact against official AtlasTool -Expand output.
/// </summary>
public sealed class AtlasFrameComposer : IDisposable
{
    private readonly List<SKBitmap> _pages = new();
    private bool _disposed;

    public AtlasFrameComposer(IEnumerable<byte[]> pagePngs)
    {
        foreach (var png in pagePngs)
        {
            var bmp = SKBitmap.Decode(png) ?? throw new InvalidDataException("Failed to decode atlas page PNG.");
            _pages.Add(bmp);
        }
    }

    public static AtlasFrameComposer FromFiles(IEnumerable<string> pagePngPaths)
        => new(pagePngPaths.Select(File.ReadAllBytes));

    public int PageCount => _pages.Count;

    public ClipFrame Compose(BatlRegion region, int frameIndex, TimeSpan? duration = null)
    {
        if (region.Page < 0 || region.Page >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(region), $"Region '{region.Name}' references missing page {region.Page}.");
        var page = _pages[region.Page];
        if (region.X < 0 || region.Y < 0 || region.X + region.Width > page.Width || region.Y + region.Height > page.Height)
            throw new InvalidDataException($"Region '{region.Name}' rect is outside page {region.Page} bounds.");

        using var surface = SKSurface.Create(new SKImageInfo(region.OriginalWidth, region.OriginalHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var subset = new SKBitmap();
        if (!page.ExtractSubset(subset, new SKRectI(region.X, region.Y, region.X + region.Width, region.Y + region.Height)))
            throw new InvalidDataException($"Failed to extract subset for region '{region.Name}'.");
        canvas.DrawBitmap(subset, region.OffsetX, region.OffsetY);

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rgba = new byte[region.OriginalWidth * region.OriginalHeight * 4];
        // SKColorType.Rgba8888 + Premul: unpremultiply on copy for UI use.
        for (int y = 0; y < region.OriginalHeight; y++)
        {
            for (int x = 0; x < region.OriginalWidth; x++)
            {
                var c = pixmap.GetPixelColor(x, y);
                int i = (y * region.OriginalWidth + x) * 4;
                rgba[i] = c.Red;
                rgba[i + 1] = c.Green;
                rgba[i + 2] = c.Blue;
                rgba[i + 3] = c.Alpha;
            }
        }

        return new ClipFrame
        {
            Index = frameIndex,
            RegionName = region.Name,
            Width = region.OriginalWidth,
            Height = region.OriginalHeight,
            Rgba = rgba,
            Duration = duration ?? TimeSpan.FromSeconds(1.0 / 30.0),
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var p in _pages)
            p.Dispose();
        _pages.Clear();
    }
}
