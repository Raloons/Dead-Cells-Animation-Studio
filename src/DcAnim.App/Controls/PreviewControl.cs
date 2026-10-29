using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DcAnim.App.Controls;

/// <summary>
/// Center viewport: checkerboard background with the current frame centered,
/// scaled by <see cref="Zoom"/> using nearest-neighbor sampling (no pixel-art blur).
/// </summary>
public sealed class PreviewControl : Control
{
    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<PreviewControl, Bitmap?>(nameof(Frame));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PreviewControl, double>(nameof(Zoom), 3.0);

    private static readonly IBrush CheckLight = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x44));
    private static readonly IBrush CheckDark = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));

    static PreviewControl()
    {
        AffectsRender<PreviewControl>(FrameProperty, ZoomProperty);
    }

    public PreviewControl()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public Bitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, 1.0, 8.0));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        // Checkerboard.
        const double cell = 14.0;
        bool flip = false;
        for (double y = 0; y < bounds.Height; y += cell, flip = !flip)
        {
            bool alt = flip;
            for (double x = 0; x < bounds.Width; x += cell, alt = !alt)
            {
                context.FillRectangle(alt ? CheckLight : CheckDark,
                    new Rect(x, y, Math.Min(cell, bounds.Width - x), Math.Min(cell, bounds.Height - y)));
            }
        }

        var frame = Frame;
        if (frame is null)
        {
            var text = new FormattedText("No animation loaded", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA8)));
            context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
            return;
        }

        double w = frame.PixelSize.Width * Zoom;
        double h = frame.PixelSize.Height * Zoom;
        var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        var source = new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        context.DrawImage(frame, source, dest);
    }
}
