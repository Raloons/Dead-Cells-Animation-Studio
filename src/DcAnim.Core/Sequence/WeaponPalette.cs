namespace DcAnim.Core.Sequence;

/// <summary>
/// Palette-band mask extraction and shaft-axis pose estimation for editable
/// weapon substitution. Native hero atlas pixels are indexed colors encoded as
/// RGBA; in examined attack clips the visible weapon occupies a distinct green
/// channel band (e.g. G=32 for pickaxe, G=159 for katana) with optional red
/// sub-ranges. This is an editing aid that bakes explicit runs - the renderer
/// never infers colors.
/// </summary>
public static class WeaponPalette
{
    public static List<PixelRun> ExtractRuns(byte[] rgba, int width, int height,
        int green, int redMin = -1, int redMax = -1)
    {
        var points = new List<(int X, int Y)>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            if (rgba[i + 3] == 0) continue;
            if (rgba[i + 1] != green) continue;
            int r = rgba[i];
            if (redMin >= 0 && (r < redMin || r > redMax)) continue;
            points.Add((x, y));
        }
        return ToRuns(points);
    }

    public static List<PixelRun> ToRuns(IEnumerable<(int X, int Y)> points)
    {
        var byRow = new SortedDictionary<int, List<int>>();
        foreach (var (x, y) in points)
        {
            if (!byRow.TryGetValue(y, out var xs)) byRow[y] = xs = new List<int>();
            xs.Add(x);
        }
        var runs = new List<PixelRun>();
        foreach (var (y, xs) in byRow)
        {
            xs.Sort();
            int start = xs[0], last = xs[0];
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i] == last + 1) { last = xs[i]; continue; }
                runs.Add(new PixelRun(y, start, last - start + 1));
                start = last = xs[i];
            }
            runs.Add(new PixelRun(y, start, last - start + 1));
        }
        return runs;
    }

    public static HashSet<(int X, int Y)> ToPoints(IEnumerable<PixelRun> runs)
    {
        var set = new HashSet<(int X, int Y)>();
        foreach (var r in runs)
        for (int x = r.X; x < r.X + r.Length; x++)
            set.Add((x, r.Y));
        return set;
    }

    public static List<PixelRun> OpaqueRuns(byte[] rgba, int width, int height)
    {
        var runs = new List<PixelRun>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                if (rgba[(y * width + x) * 4 + 3] == 0) { x++; continue; }
                int start = x;
                while (x < width && rgba[(y * width + x) * 4 + 3] != 0) x++;
                runs.Add(new PixelRun(y, start, x - start));
            }
        }
        return runs;
    }

    public static List<PixelRun> Subtract(List<PixelRun> base_, HashSet<(int X, int Y)> remove)
    {
        var kept = new List<(int X, int Y)>();
        foreach (var r in base_)
        for (int x = r.X; x < r.X + r.Length; x++)
            if (!remove.Contains((x, r.Y))) kept.Add((x, r.Y));
        return ToRuns(kept);
    }

    /// <summary>Principal axis of weapon pixels, in degrees, with centroid.</summary>
    public static (double Degrees, double Cx, double Cy, int Count) ShaftAxis(HashSet<(int X, int Y)> points)
    {
        if (points.Count == 0) throw new ArgumentException("No weapon pixels for axis estimation");
        double cx = points.Average(p => p.X + 0.5), cy = points.Average(p => p.Y + 0.5);
        double xx = 0, yy = 0, xy = 0;
        foreach (var (x, y) in points)
        {
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            xx += dx * dx; yy += dy * dy; xy += dx * dy;
        }
        xx /= points.Count; yy /= points.Count; xy /= points.Count;
        double theta = 0.5 * Math.Atan2(2 * xy, xx - yy);
        double major = (xx + yy + Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / 2;
        double minor = Math.Max(1e-8, (xx + yy - Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / 2);
        if (major < 2 * minor)
            throw new InvalidOperationException("Weapon pixels lack shaft elongation for orientation");
        return (Math.Atan2(Math.Sin(theta), Math.Cos(theta)) * 180 / Math.PI, cx, cy, points.Count);
    }

    public static (double X, double Y) NearestPoint(HashSet<(int X, int Y)> points, double hx, double hy)
    {
        double best = double.MaxValue; (double X, double Y) result = (hx, hy);
        foreach (var (x, y) in points.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            double dx = x + 0.5 - hx, dy = y + 0.5 - hy;
            double d = dx * dx + dy * dy;
            if (d < best) { best = d; result = (x + 0.5, y + 0.5); }
        }
        return result;
    }

    /// <summary>
    /// Estimate a replacement pose aligning the donor blade axis to the body
    /// shaft axis. Hand positions are optional; without them the grip defaults
    /// to the shaft centroid refined to the nearest actual pixel.
    /// </summary>
    public static WeaponPose EstimatePose(
        HashSet<(int X, int Y)> shaftPoints, (double Degrees, double Cx, double Cy) shaftAxis,
        HashSet<(int X, int Y)> donorPoints, (double Degrees, double Cx, double Cy) donorAxis,
        (double X, double Y) donorPivot,
        double? handX = null, double? handY = null)
    {
        // Sign the shaft direction toward the head end (away from hand when known,
        // otherwise toward the farthest pixel cluster from the centroid).
        double sx = Math.Cos(shaftAxis.Degrees * Math.PI / 180);
        double sy = Math.Sin(shaftAxis.Degrees * Math.PI / 180);
        double hx = handX ?? shaftAxis.Cx, hy = handY ?? shaftAxis.Cy;
        // Use the shaft pixel farthest from the hand as the head direction.
        double fx = hx, fy = hy; double best = -1;
        foreach (var (x, y) in shaftPoints)
        {
            double dx = x + 0.5 - hx, dy = y + 0.5 - hy;
            double d = dx * dx + dy * dy;
            if (d > best) { best = d; fx = x + 0.5; fy = y + 0.5; }
        }
        double dirX = fx - hx, dirY = fy - hy;
        if (dirX * sx + dirY * sy < 0) { sx = -sx; sy = -sy; }
        double shaftDeg = Math.Atan2(sy, sx) * 180 / Math.PI;

        double degrees = shaftDeg - donorAxis.Degrees;
        while (degrees > 180) degrees -= 360;
        while (degrees <= -180) degrees += 360;

        var grip = NearestPoint(shaftPoints, hx, hy);
        if (handX.HasValue && handY.HasValue)
        {
            double dx = grip.X - handX.Value, dy = grip.Y - handY.Value;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > 8)
                grip = (shaftAxis.Cx, shaftAxis.Cy); // hand track mismatch: fall back to shaft center
        }
        return new WeaponPose(grip.X, grip.Y, degrees, 1, 1, false);
    }
}
