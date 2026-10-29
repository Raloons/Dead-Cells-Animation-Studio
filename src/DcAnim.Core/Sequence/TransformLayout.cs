namespace DcAnim.Core.Sequence;

/// <summary>
/// Exact canvas-space mapping used by both rendering and editor overlays, so
/// selection rectangles and pivot markers line up with drawn pixels.
/// Transform order matches <c>SequenceCompositor</c>:
/// T(canvasCenter + position) · R(rotation) · S(scale·flip) · T(−pivot·size).
/// </summary>
public static class TransformLayout
{
    public static (double X, double Y) PivotPoint(
        Transform t, double canvasWidth, double canvasHeight) =>
        (canvasWidth / 2 + t.X, canvasHeight / 2 + t.Y);

    /// <summary>Axis-aligned bounds of the transformed source rect.</summary>
    public static (double X, double Y, double Width, double Height) MapRect(
        double sourceWidth, double sourceHeight,
        Transform t, double canvasWidth, double canvasHeight)
    {
        double sx = t.ScaleX * (t.FlipH ? -1 : 1);
        double sy = t.ScaleY * (t.FlipV ? -1 : 1);
        double rad = t.RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double px = t.PivotX * sourceWidth, py = t.PivotY * sourceHeight;
        double cx = canvasWidth / 2 + t.X, cy = canvasHeight / 2 + t.Y;

        double MapX(double lx, double ly) =>
            cx + ((lx - px) * sx) * cos - ((ly - py) * sy) * sin;
        double MapY(double lx, double ly) =>
            cy + ((lx - px) * sx) * sin + ((ly - py) * sy) * cos;

        double[] xs = { MapX(0, 0), MapX(sourceWidth, 0), MapX(0, sourceHeight), MapX(sourceWidth, sourceHeight) };
        double[] ys = { MapY(0, 0), MapY(sourceWidth, 0), MapY(0, sourceHeight), MapY(sourceWidth, sourceHeight) };
        double x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
