using System.Runtime.InteropServices;
using DcAnim.Core.Model;
using SkiaSharp;

namespace DcAnim.Core.Atlas;

/// <summary>
/// Composes animation frames from a parsed BATL atlas plus its page PNGs.
/// Each region is cropped from its page and pasted top-left at (OffsetX, OffsetY)
/// onto a transparent canvas of (OriginalWidth, OriginalHeight) - semantics
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
        int width = region.OriginalWidth;
        int height = region.OriginalHeight;
        var rgba = new byte[width * height * 4];
        // Bulk copy, then unpremultiply in managed code (per-pixel GetPixelColor is ~50x slower).
        int rowBytes = (int)pixmap.RowBytes;
        var row = new byte[rowBytes];
        nint src = pixmap.GetPixels();
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(src + y * rowBytes, row, 0, Math.Min(rowBytes, width * 4));
            for (int x = 0; x < width; x++)
            {
                int s = x * 4;
                int d = (y * width + x) * 4;
                byte a = row[s + 3];
                if (a == 0)
                {
                    rgba[d] = rgba[d + 1] = rgba[d + 2] = rgba[d + 3] = 0;
                }
                else
                {
                    rgba[d] = (byte)Math.Min(255, (row[s] * 255 + a / 2) / a);
                    rgba[d + 1] = (byte)Math.Min(255, (row[s + 1] * 255 + a / 2) / a);
                    rgba[d + 2] = (byte)Math.Min(255, (row[s + 2] * 255 + a / 2) / a);
                    rgba[d + 3] = a;
                }
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
