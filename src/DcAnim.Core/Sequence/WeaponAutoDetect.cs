namespace DcAnim.Core.Sequence;

/// <summary>
/// Fills a <see cref="WeaponClipOverride"/> from palette-band masks and
/// shaft-axis pose estimation. All output is explicit runs/poses for the
/// renderer; nothing is inferred at render time.
/// </summary>
public static class WeaponAutoDetect
{
    public static WeaponClipOverride Build(
        string donorAnimationId, int donorFrame,
        int bodyMaskGreen, int donorMaskGreen,
        IReadOnlyList<(int SourceFrame, RgbaImage Image)> bodies,
        RgbaImage donorNative,
        bool preserveBody = true, bool behindBody = false,
        bool nonBodyMode = false, int bodyColorGreen = 96)
    {
        if (bodies.Count == 0) throw new ArgumentException("No body frames supplied");
        var donorPoints = nonBodyMode
            ? NonBodyPoints(donorNative, bodyColorGreen)
            : PointsWhere(donorNative, donorMaskGreen);
        if (donorPoints.Count == 0)
            throw new InvalidDataException($"No donor weapon pixels (G={donorMaskGreen}) in {donorAnimationId}[{donorFrame}].");
        var donorRuns = WeaponPalette.ToRuns(donorPoints);
        var (donorDeg, donorCx, donorCy, _) = WeaponPalette.ShaftAxis(donorPoints);
        // Grip defaults to the donor centroid; the user refines it in the inspector.
        double pivotX = donorCx, pivotY = donorCy;

        var weapon = new WeaponClipOverride
        {
            Enabled = true,
            DonorAnimationId = donorAnimationId,
            DonorFrame = donorFrame,
            DonorRegion = donorRuns,
            DonorPivotX = pivotX,
            DonorPivotY = pivotY,
            BehindBody = behindBody,
            PreserveBody = preserveBody,
            BodyMaskGreen = bodyMaskGreen,
            DonorMaskGreen = donorMaskGreen,
            NonBodyMode = nonBodyMode,
            BodyColorGreen = bodyColorGreen,
        };

        foreach (var (src, img) in bodies)
        {
            var shaft = nonBodyMode ? NonBodyPoints(img, bodyColorGreen) : PointsWhere(img, bodyMaskGreen);
            if (shaft.Count == 0) continue;
            WeaponPalette.ShaftAxis(shaft); // validates elongation; throws on blobs
            var (shaftDeg, shaftCx, shaftCy, _) = WeaponPalette.ShaftAxis(shaft);
            var pose = WeaponPalette.EstimatePose(shaft,
                (shaftDeg, shaftCx, shaftCy), donorPoints, (donorDeg, donorCx, donorCy),
                (pivotX, pivotY));
            if (behindBody) pose = pose with { BehindBody = true };
            weapon.Frames.Add(new WeaponFrameOverride
            {
                SourceFrame = src,
                Remove = WeaponPalette.ToRuns(shaft),
                Foreground = new List<PixelRun>(),
                Pose = pose,
            });
        }
        if (weapon.Frames.Count == 0)
            throw new InvalidDataException("Auto-detect found no weapon pixels in the body range.");
        return weapon;
    }

    private static HashSet<(int X, int Y)> PointsWhere(RgbaImage img, int green)
    {
        var set = new HashSet<(int X, int Y)>();
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            int i = (y * img.Width + x) * 4;
            if (img.Pixels[i + 3] == 0) continue;
            if (img.Pixels[i + 1] == green) set.Add((x, y));
        }
        return set;
    }

    private static HashSet<(int X, int Y)> NonBodyPoints(RgbaImage img, int bodyGreen)
    {
        var set = new HashSet<(int X, int Y)>();
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            int i = (y * img.Width + x) * 4;
            if (img.Pixels[i + 3] == 0) continue;
            if (img.Pixels[i + 1] != bodyGreen) set.Add((x, y));
        }
        return set;
    }
}
