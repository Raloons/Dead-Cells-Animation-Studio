using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DcAnim.Core.Model;

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

        using var locked = bitmap.Lock();
        unsafe
        {
            byte* dst = (byte*)locked.Address;
            var src = frame.Rgba;
            int n = frame.Width * frame.Height;
            for (int i = 0; i < n; i++)
            {
                byte r = src[i * 4];
                byte g = src[i * 4 + 1];
                byte b = src[i * 4 + 2];
                byte a = src[i * 4 + 3];
                // Straight -> premultiplied.
                dst[i * 4] = (byte)((b * a + 127) / 255);
                dst[i * 4 + 1] = (byte)((g * a + 127) / 255);
                dst[i * 4 + 2] = (byte)((r * a + 127) / 255);
                dst[i * 4 + 3] = a;
            }
        }
        return bitmap;
    }
}
