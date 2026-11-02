using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcAnim.Core.Sequence;

/// <summary>
/// BTD-compatible portable plan IO (native-weapon-composition/1). Studio is the
/// editor; BTD is a consumer. No game assets are embedded — only provenance,
/// runs, pivots and poses plus pixel digests for outfit guards.
/// </summary>
public static class WeaponPlanIO
{
    private sealed class PlanDto
    {
        public string Schema { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        public AppearanceDto Weapon { get; set; } = new();
        public List<FrameDto> Frames { get; set; } = new();
    }

    private sealed class AppearanceDto
    {
        public SourceDto Source { get; set; } = new();
        public List<RunDto> Region { get; set; } = new();
        public string PixelsSha256 { get; set; } = string.Empty;
        public double PivotX { get; set; }
        public double PivotY { get; set; }
    }

    private sealed class FrameDto
    {
        public SourceDto Source { get; set; } = new();
        public List<RunDto> Remove { get; set; } = new();
        public string RemovedPixelsSha256 { get; set; } = string.Empty;
        public PoseDto Pose { get; set; } = new();
        public List<RunDto> Foreground { get; set; } = new();
        public List<object> Corrections { get; set; } = new();
    }

    private sealed class SourceDto
    {
        public string AnimationId { get; set; } = string.Empty;
        public int SourceFrame { get; set; }
    }

    private sealed class RunDto
    {
        public int Y { get; set; }
        public int X { get; set; }
        public int Length { get; set; }
    }

    private sealed class PoseDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Degrees { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public bool BehindBody { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Export(string id, string bodyAnimationId, int canvasWidth, int canvasHeight,
        WeaponClipOverride weapon, Func<string, int, RgbaImage> resolve)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Plan id required", nameof(id));
        if (weapon.DonorRegion.Count == 0) throw new InvalidDataException("Donor region is empty; run Auto-detect first.");
        if (weapon.Frames.Count == 0) throw new InvalidDataException("No weapon frames; run Auto-detect first.");
        var donorImg = PadTo(resolve(weapon.DonorAnimationId, weapon.DonorFrame), canvasWidth, canvasHeight);
        var dto = new PlanDto
        {
            Schema = "native-weapon-composition/1",
            Id = id,
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            Weapon = new AppearanceDto
            {
                Source = new SourceDto { AnimationId = weapon.DonorAnimationId, SourceFrame = weapon.DonorFrame },
                Region = weapon.DonorRegion.Select(r => new RunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                PixelsSha256 = WeaponComposition.MaskDigest(donorImg, weapon.DonorRegion),
                PivotX = weapon.DonorPivotX,
                PivotY = weapon.DonorPivotY,
            },
            Frames = weapon.Frames.OrderBy(f => f.SourceFrame).Select(f =>
            {
                var bodyImg = resolve(bodyAnimationId, f.SourceFrame);
                if (bodyImg.Width != canvasWidth || bodyImg.Height != canvasHeight)
                    throw new InvalidDataException($"Body frame {f.SourceFrame} canvas {bodyImg.Width}x{bodyImg.Height} differs from plan {canvasWidth}x{canvasHeight}.");
                return new FrameDto
                {
                    Source = new SourceDto { AnimationId = bodyAnimationId, SourceFrame = f.SourceFrame },
                    Remove = f.Remove.Select(r => new RunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                    RemovedPixelsSha256 = WeaponComposition.MaskDigest(bodyImg, f.Remove),
                    Pose = new PoseDto { X = f.Pose.X, Y = f.Pose.Y, Degrees = f.Pose.Degrees, ScaleX = f.Pose.ScaleX, ScaleY = f.Pose.ScaleY, BehindBody = f.Pose.BehindBody || weapon.BehindBody },
                    Foreground = f.Foreground.Select(r => new RunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                    Corrections = new List<object>(),
                };
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, Json) + "\n";
    }

    public static (string BodyAnimationId, WeaponClipOverride Weapon, int CanvasWidth, int CanvasHeight, string PlanId) Import(string json)
    {
        var dto = JsonSerializer.Deserialize<PlanDto>(json, Json)
            ?? throw new InvalidDataException("Empty weapon plan.");
        if (dto.Schema != "native-weapon-composition/1")
            throw new InvalidDataException($"Unsupported weapon plan schema '{dto.Schema}'.");
        if (dto.CanvasWidth <= 0 || dto.CanvasHeight <= 0 || dto.CanvasWidth > 4096 || dto.CanvasHeight > 4096)
            throw new InvalidDataException("Invalid plan canvas.");
        if (dto.Frames.Count == 0) throw new InvalidDataException("Plan has no frames.");
        string body = dto.Frames[0].Source.AnimationId;
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("Plan frame missing body animation.");
        if (dto.Frames.Any(f => f.Source.AnimationId != body))
            throw new InvalidDataException("Multi-animation plans cannot attach to a single clip; split by body animation first.");
        var weapon = new WeaponClipOverride
        {
            Enabled = true,
            DonorAnimationId = dto.Weapon.Source.AnimationId,
            DonorFrame = dto.Weapon.Source.SourceFrame,
            DonorRegion = dto.Weapon.Region.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
            DonorPivotX = dto.Weapon.PivotX,
            DonorPivotY = dto.Weapon.PivotY,
            PreserveBody = true,
            Frames = dto.Frames.Select(f => new WeaponFrameOverride
            {
                SourceFrame = f.Source.SourceFrame,
                Remove = f.Remove.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
                Foreground = f.Foreground.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
                Pose = new WeaponPose(f.Pose.X, f.Pose.Y, f.Pose.Degrees, f.Pose.ScaleX, f.Pose.ScaleY, f.Pose.BehindBody),
            }).ToList(),
        };
        return (body, weapon, dto.CanvasWidth, dto.CanvasHeight, dto.Id);
    }

    private static RgbaImage PadTo(RgbaImage img, int w, int h)
    {
        if (img.Width == w && img.Height == h) return img;
        if (img.Width > w || img.Height > h)
            throw new InvalidDataException($"Donor canvas {img.Width}x{img.Height} exceeds body canvas {w}x{h}.");
        var padded = new RgbaImage(w, h, new byte[w * h * 4]);
        for (int y = 0; y < img.Height; y++)
            Array.Copy(img.Pixels, y * img.Width * 4, padded.Pixels, y * w * 4, img.Width * 4);
        return padded;
    }
}
