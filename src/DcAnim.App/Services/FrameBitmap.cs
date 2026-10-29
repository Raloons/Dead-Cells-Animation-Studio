using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DcAnim.Core.Model;
using SkiaSharp;

namespace DcAnim.App.Services;

/// <summary>Converts decoder RGBA frames to Avalonia bitmaps (premultiplied BGRA).</summary>
public static class FrameBitmap
{
    public static WriteableBitmap ToWriteableBitmap(ClipFrame frame)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        WritePremultiplied(bitmap, frame.Rgba);
        return bitmap;
    }

    /// <summary>Downscales a frame to a square thumbnail of <paramref name="size"/> px.</summary>
    public static WriteableBitmap ToThumbnail(ClipFrame frame, int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        // Wrap the decoded buffer directly (no per-pixel copy), resize natively,
        // then bulk-copy + unpremultiply.
        var handle = GCHandle.Alloc(frame.Rgba, GCHandleType.Pinned);
        try
        {
            using var src = new SKBitmap(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            src.InstallPixels(
                new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(),
                frame.Width * 4);
            using var scaled = src.Resize(
                new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul),
                SKFilterQuality.None);
            if (scaled is null)
                throw new InvalidOperationException("Failed to resize thumbnail.");
            var rgba = new byte[size * size * 4];
            Marshal.Copy(scaled.GetPixels(), rgba, 0, rgba.Length);
            UnpremultiplyInPlace(rgba);

            var bitmap = new WriteableBitmap(
                new PixelSize(size, size),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            WritePremultiplied(bitmap, rgba);
            return bitmap;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void UnpremultiplyInPlace(byte[] rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            if (a == 0 || a == 255)
                continue;
            rgba[i] = (byte)Math.Min(255, (rgba[i] * 255 + a / 2) / a);
            rgba[i + 1] = (byte)Math.Min(255, (rgba[i + 1] * 255 + a / 2) / a);
            rgba[i + 2] = (byte)Math.Min(255, (rgba[i + 2] * 255 + a / 2) / a);
        }
    }

    /// <summary>Bitmap from straight-alpha RGBA bytes (e.g. a composed sequence frame).</summary>
    public static WriteableBitmap FromRgba(byte[] rgba, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        WritePremultiplied(bitmap, rgba);
        return bitmap;
    }

    private static void WritePremultiplied(WriteableBitmap bitmap, byte[] rgba)
    {
        using var locked = bitmap.Lock();
        unsafe
        {
            byte* dst = (byte*)locked.Address;
            int n = rgba.Length / 4;
            for (int i = 0; i < n; i++)
            {
                byte r = rgba[i * 4];
                byte g = rgba[i * 4 + 1];
                byte b = rgba[i * 4 + 2];
                byte a = rgba[i * 4 + 3];
                // Straight -> premultiplied.
                dst[i * 4] = (byte)((b * a + 127) / 255);
                dst[i * 4 + 1] = (byte)((g * a + 127) / 255);
                dst[i * 4 + 2] = (byte)((r * a + 127) / 255);
                dst[i * 4 + 3] = a;
            }
        }
    }
}
