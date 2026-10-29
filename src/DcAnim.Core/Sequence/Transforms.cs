namespace DcAnim.Core.Sequence;

/// <summary>Keyframeable transform property. Flips are discrete (not interpolated).</summary>
public enum TransformProperty
{
    X, Y, ScaleX, ScaleY, RotationDegrees, Opacity, PivotX, PivotY,
}

public enum KeyframeInterpolation { Hold, Linear }

/// <summary>
/// Non-destructive per-frame transform. Position places the <see cref="Pivot"/>
/// point (normalized 0..1 in source-frame space) on the sequence canvas.
/// Scale flips are expressed as sign via <see cref="FlipH"/>/<see cref="FlipV"/>
/// and multiply the scale factors. Rotation is degrees clockwise. Opacity
/// 0..1. Rendered with nearest-neighbor sampling; matrix order:
/// T(position) · R(rotation) · S(scale·flip) · T(−pivot·size).
/// </summary>
public sealed record Transform
{
    public double X { get; init; }
    public double Y { get; init; }
    public double ScaleX { get; init; } = 1.0;
    public double ScaleY { get; init; } = 1.0;
    public double RotationDegrees { get; init; }
    public double Opacity { get; init; } = 1.0;
    public double PivotX { get; init; } = 0.5;
    public double PivotY { get; init; } = 0.5;
    public bool FlipH { get; init; }
    public bool FlipV { get; init; }

    public static Transform Identity => new();

    public double Get(TransformProperty property) => property switch
    {
        TransformProperty.X => X,
        TransformProperty.Y => Y,
        TransformProperty.ScaleX => ScaleX,
        TransformProperty.ScaleY => ScaleY,
        TransformProperty.RotationDegrees => RotationDegrees,
        TransformProperty.Opacity => Opacity,
        TransformProperty.PivotX => PivotX,
        TransformProperty.PivotY => PivotY,
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };

    public Transform With(TransformProperty property, double value) => property switch
    {
        TransformProperty.X => this with { X = value },
        TransformProperty.Y => this with { Y = value },
        TransformProperty.ScaleX => this with { ScaleX = value },
        TransformProperty.ScaleY => this with { ScaleY = value },
        TransformProperty.RotationDegrees => this with { RotationDegrees = value },
        TransformProperty.Opacity => this with { Opacity = Math.Clamp(value, 0.0, 1.0) },
        TransformProperty.PivotX => this with { PivotX = value },
        TransformProperty.PivotY => this with { PivotY = value },
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };
}

/// <summary>A value at a clip-local output frame.</summary>
public sealed record TransformKeyframe(TransformProperty Property, int Frame, double Value, KeyframeInterpolation Interpolation = KeyframeInterpolation.Hold);

/// <summary>
/// Evaluates one property's keyframes at a clip-local frame. Deterministic:
/// before the first key the base value holds; Hold keeps the latest key;
/// Linear interpolates between bracketing keys (plain numeric lerp, even for
/// rotation — documented, no angle unwrapping surprises).
/// </summary>
public static class KeyframeEvaluator
{
    public static double Evaluate(
        IReadOnlyList<TransformKeyframe> keys,
        TransformProperty property,
        int frame,
        double baseValue)
    {
        TransformKeyframe? prev = null, next = null;
        foreach (var k in keys)
        {
            if (k.Property != property)
                continue;
            if (k.Frame <= frame)
            {
                if (prev is null || k.Frame >= prev.Frame)
                    prev = k;
            }
            else if (next is null || k.Frame < next.Frame)
            {
                next = k;
            }
        }
        if (prev is null)
            return baseValue;
        if (prev.Interpolation == KeyframeInterpolation.Hold || next is null || next.Frame == prev.Frame)
            return prev.Value;
        double t = (double)(frame - prev.Frame) / (next.Frame - prev.Frame);
        return prev.Value + (next.Value - prev.Value) * t;
    }

    public static Transform EvaluateAll(
        IReadOnlyList<TransformKeyframe> keys, int frame, Transform baseTransform)
    {
        var t = baseTransform;
        foreach (TransformProperty p in Enum.GetValues<TransformProperty>())
            t = t.With(p, Evaluate(keys, p, frame, baseTransform.Get(p)));
        return t;
    }
}
