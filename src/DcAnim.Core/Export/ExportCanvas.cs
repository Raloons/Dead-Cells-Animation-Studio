using DcAnim.Core.Model;
using SkiaSharp;

namespace DcAnim.Core.Export;

/// <summary>Progress for export operations (0..1) with an optional message.</summary>
public sealed record ExportProgress(double Fraction, string Message);

/// <summary>
/// Shared canvas logic: the sequence canvas is the max frame extent; every
/// frame is centered on it (integer pixel offsets). One coordinate system
/// for preview and all exporters - see <c>docs/AS-M3-REPORT.md</c>.
/// </summary>
public static class ExportCanvas
{
    public static (int Width, int Height) Measure(IReadOnlyList<ClipFrame> frames)
    {
        int w = 0, h = 0;
        foreach (var f in frames)
        {
            w = Math.Max(w, f.Width);
            h = Math.Max(h, f.Height);
        }
        if (w <= 0 || h <= 0)
            throw new InvalidDataException("Cannot export an empty sequence.");
        return (w, h);
    }

    /// <summary>Top-left paste offset that centers <paramref name="frame"/> on the canvas.</summary>
    public static (int X, int Y) CenterOffset(ClipFrame frame, int canvasWidth, int canvasHeight) =>
        ((canvasWidth - frame.Width) / 2, (canvasHeight - frame.Height) / 2);

    /// <summary>Compose every frame centered on the shared canvas.</summary>
    public static (int Width, int Height, List<byte[]> Frames) CenteredFrames(IReadOnlyList<ClipFrame> frames)
    {
        var (w, h) = Measure(frames);
        var list = new List<byte[]>(frames.Count);
        foreach (var f in frames)
            list.Add(ComposeCentered(f, w, h));
        return (w, h, list);
    }

    /// <summary>Composes a frame centered on a transparent canvas (RGBA bytes).</summary>
    public static byte[] ComposeCentered(ClipFrame frame, int canvasWidth, int canvasHeight)
    {
        var (ox, oy) = CenterOffset(frame, canvasWidth, canvasHeight);
        var canvas = new byte[canvasWidth * canvasHeight * 4];
        for (int y = 0; y < frame.Height; y++)
        {
            Buffer.BlockCopy(frame.Rgba, y * frame.Width * 4,
                canvas, ((oy + y) * canvasWidth + ox) * 4, frame.Width * 4);
        }
        return canvas;
    }

    public static SKBitmap ToSkBitmap(byte[] rgba, int width, int height)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        nint ptr = bmp.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, ptr, rgba.Length);
        return bmp;
    }
}
