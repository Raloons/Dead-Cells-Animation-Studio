using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcAnim.Core.Sequence;

/// <summary>Validation result for opening a project against a native catalog.</summary>
public sealed record ProjectValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> MissingAnimationIds)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Human-readable <c>.dcanim</c> JSON persistence. v2 (current) adds clip
/// transforms, keyframes, effect/audio tracks and canvas settings; v1 files
/// (AS-M3: clips only) load directly with documented defaults (identity
/// transforms, empty keyframe lists, no tracks).
/// Asset references stay as paths (relative to the project file when
/// practical) - never embedded artwork.
/// </summary>
public static class ProjectSerializer
{
    public const string FormatTag = "dcanim";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class ProjectDto
    {
        public string Format { get; set; } = FormatTag;
        public int Version { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Fps { get; set; }
        public List<ClipDto> Clips { get; set; } = new();
        public List<EffectDto> Effects { get; set; } = new();
        public List<AudioDto> Audio { get; set; } = new();
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        public bool RippleFxAudio { get; set; } = true;
    }

    private sealed class ClipDto
    {
        public string Id { get; set; } = string.Empty;
        public string Animation { get; set; } = string.Empty;
        public double Speed { get; set; } = 1.0;
        public TransformDto? Transform { get; set; }
        public List<KeyframeDto> Keyframes { get; set; } = new();
        public int SourceFirst { get; set; }
        public int? SourceLast { get; set; }
        public bool Reversed { get; set; }
        public int LeadHoldFrames { get; set; }
        public int TailHoldFrames { get; set; }
        public WeaponDto? Weapon { get; set; }
    }

    private sealed class WeaponDto
    {
        public bool Enabled { get; set; } = true;
        public string DonorAnimation { get; set; } = string.Empty;
        public int DonorFrame { get; set; }
        public List<PixelRunDto> DonorRegion { get; set; } = new();
        public double DonorPivotX { get; set; }
        public double DonorPivotY { get; set; }
        public bool BehindBody { get; set; }
        public bool PreserveBody { get; set; } = true;
        public int BodyMaskGreen { get; set; } = 32;
        public int DonorMaskGreen { get; set; } = 159;
        public bool NonBodyMode { get; set; }
        public int BodyColorGreen { get; set; } = 96;
        public List<WeaponFrameDto> Frames { get; set; } = new();
    }

    private sealed class WeaponFrameDto
    {
        public int SourceFrame { get; set; }
        public List<PixelRunDto> Remove { get; set; } = new();
        public List<PixelRunDto> Foreground { get; set; } = new();
        public WeaponPoseDto Pose { get; set; } = new();
    }

    private sealed class PixelRunDto
    {
        public int Y { get; set; }
        public int X { get; set; }
        public int Length { get; set; }
    }

    private sealed class WeaponPoseDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Degrees { get; set; }
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public bool BehindBody { get; set; }
    }

    private sealed class TransformDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public double RotationDegrees { get; set; }
        public double Opacity { get; set; } = 1.0;
        public double PivotX { get; set; } = 0.5;
        public double PivotY { get; set; } = 0.5;
        public bool FlipH { get; set; }
        public bool FlipV { get; set; }
    }

    private sealed class KeyframeDto
    {
        public string Property { get; set; } = string.Empty;
        public int Frame { get; set; }
        public double Value { get; set; }
        public string Interpolation { get; set; } = "Hold";
    }

    private sealed class EffectDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public int StartFrame { get; set; }
        public int DurationFrames { get; set; } = 30;
        public int Layer { get; set; }
        public TransformDto? Transform { get; set; }
        public List<KeyframeDto> Keyframes { get; set; } = new();
    }

    private sealed class AudioDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public double StartSeconds { get; set; }
        public double TrimStartSeconds { get; set; }
        public double? DurationSeconds { get; set; }
        public double Volume { get; set; } = 1.0;
        public bool Muted { get; set; }
    }

    public static void Save(SequenceProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateStructure(project, out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException("Cannot save invalid project: " + string.Join("; ", errors));

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        var dto = new ProjectDto
        {
            Version = SequenceProject.CurrentVersion,
            Name = project.Name,
            Fps = project.Fps,
            Clips = project.Clips.Select(c => new ClipDto
            {
                Id = c.Id,
                Animation = c.AnimationId,
                Speed = c.Speed,
                Transform = ToDto(c.BaseTransform),
                Keyframes = c.Keyframes.Select(ToDto).ToList(),
                SourceFirst = c.SourceFirst,
                SourceLast = c.SourceLast,
                Reversed = c.Reversed,
                LeadHoldFrames = c.LeadHoldFrames,
                TailHoldFrames = c.TailHoldFrames,
                Weapon = c.Weapon is null ? null : new WeaponDto
                {
                    Enabled = c.Weapon.Enabled,
                    DonorAnimation = c.Weapon.DonorAnimationId,
                    DonorFrame = c.Weapon.DonorFrame,
                    DonorRegion = c.Weapon.DonorRegion.Select(r => new PixelRunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                    DonorPivotX = c.Weapon.DonorPivotX,
                    DonorPivotY = c.Weapon.DonorPivotY,
                    BehindBody = c.Weapon.BehindBody,
                    PreserveBody = c.Weapon.PreserveBody,
                    BodyMaskGreen = c.Weapon.BodyMaskGreen,
                    DonorMaskGreen = c.Weapon.DonorMaskGreen,
                    NonBodyMode = c.Weapon.NonBodyMode,
                    BodyColorGreen = c.Weapon.BodyColorGreen,
                    Frames = c.Weapon.Frames.Select(f => new WeaponFrameDto
                    {
                        SourceFrame = f.SourceFrame,
                        Remove = f.Remove.Select(r => new PixelRunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                        Foreground = f.Foreground.Select(r => new PixelRunDto { Y = r.Y, X = r.X, Length = r.Length }).ToList(),
                        Pose = new WeaponPoseDto { X = f.Pose.X, Y = f.Pose.Y, Degrees = f.Pose.Degrees, ScaleX = f.Pose.ScaleX, ScaleY = f.Pose.ScaleY, BehindBody = f.Pose.BehindBody },
                    }).ToList(),
                },
            }).ToList(),
            Effects = project.Effects.Select(e => new EffectDto
            {
                Id = e.Id,
                Name = e.Name,
                Image = Relativize(e.ImagePath, dir),
                StartFrame = e.StartFrame,
                DurationFrames = e.DurationFrames,
                Layer = e.Layer,
                Transform = ToDto(e.BaseTransform),
                Keyframes = e.Keyframes.Select(ToDto).ToList(),
            }).ToList(),
            Audio = project.Audio.Select(a => new AudioDto
            {
                Id = a.Id,
                Name = a.Name,
                File = Relativize(a.AudioPath, dir),
                StartSeconds = a.StartSeconds,
                TrimStartSeconds = a.TrimStartSeconds,
                DurationSeconds = a.DurationSeconds,
                Volume = a.Volume,
                Muted = a.Muted,
            }).ToList(),
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            RippleFxAudio = project.RippleFxAudio,
        };
        Directory.CreateDirectory(dir!);
        string tmp = Path.Combine(dir!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Options));
            // Atomic replace: readers never see a half-written project, and a
            // failed save cannot corrupt the existing file.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static SequenceProject Load(string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        string json;
        try
        {
            json = File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Cannot read project file '{path}'.", ex);
        }

        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Project file '{path}' is not valid JSON.", ex);
        }
        if (dto is null)
            throw new InvalidDataException($"Project file '{path}' is empty or malformed.");
        if (!string.Equals(dto.Format, FormatTag, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{path}' is not a Dead Cells Animation Studio project (bad format tag).");
        if (dto.Version != 1 && dto.Version != 2 && dto.Version != 3 && dto.Version != SequenceProject.CurrentVersion)
            throw new NotSupportedException(
                $"Unsupported project version {dto.Version} (this build supports versions 1 through {SequenceProject.CurrentVersion}).");

        var project = FromDto(dto, dir);
        ValidateStructure(project, out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"Project file '{path}' failed validation: " + string.Join("; ", errors));
        return project;
    }

    /// <summary>Resolve a stored asset reference against the project location.</summary>
    public static string ResolveAsset(string stored, string? projectDir)
    {
        if (Path.IsPathRooted(stored) || string.IsNullOrEmpty(projectDir))
            return stored;
        return Path.GetFullPath(Path.Combine(projectDir, stored));
    }

    private static string Relativize(string p, string? dir)
    {
        if (string.IsNullOrWhiteSpace(p) || string.IsNullOrEmpty(dir) || !Path.IsPathRooted(p))
            return p ?? string.Empty; // already relative (or empty): store as-is
        try
        {
            // Forward slashes keep project files portable across platforms.
            return Path.GetRelativePath(dir, p).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return p;
        }
    }

    private static SequenceProject FromDto(ProjectDto dto, string? dir) => new()
    {
        Version = SequenceProject.CurrentVersion,
        Name = string.IsNullOrWhiteSpace(dto.Name) ? "Untitled" : dto.Name,
        Fps = dto.Fps,
        CanvasWidth = dto.CanvasWidth,
        CanvasHeight = dto.CanvasHeight,
        RippleFxAudio = dto.RippleFxAudio, // default true covers v1/v2 files
        Clips = dto.Clips.Select(c => new SequenceClip
        {
            Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id,
            AnimationId = c.Animation ?? string.Empty,
            Speed = c.Speed,
            BaseTransform = FromDto(c.Transform),
            Keyframes = c.Keyframes.Select(FromDto).Where(k => k is not null).Select(k => k!).ToList(),
            // v1/v2 files predate trims: full range, forward, no holds.
            // Stored values pass through untouched so structural validation
            // can reject bad files (no silent coercion here).
            SourceFirst = c.SourceFirst,
            SourceLast = c.SourceLast,
            Reversed = c.Reversed,
            LeadHoldFrames = c.LeadHoldFrames,
            TailHoldFrames = c.TailHoldFrames,
            Weapon = c.Weapon is null ? null : new WeaponClipOverride
            {
                Enabled = c.Weapon.Enabled,
                DonorAnimationId = c.Weapon.DonorAnimation ?? string.Empty,
                DonorFrame = c.Weapon.DonorFrame,
                DonorRegion = c.Weapon.DonorRegion.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
                DonorPivotX = c.Weapon.DonorPivotX,
                DonorPivotY = c.Weapon.DonorPivotY,
                BehindBody = c.Weapon.BehindBody,
                PreserveBody = c.Weapon.PreserveBody,
                BodyMaskGreen = c.Weapon.BodyMaskGreen,
                DonorMaskGreen = c.Weapon.DonorMaskGreen,
                NonBodyMode = c.Weapon.NonBodyMode,
                BodyColorGreen = c.Weapon.BodyColorGreen,
                Frames = c.Weapon.Frames.Select(f => new WeaponFrameOverride
                {
                    SourceFrame = f.SourceFrame,
                    Remove = f.Remove.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
                    Foreground = f.Foreground.Select(r => new PixelRun(r.Y, r.X, r.Length)).ToList(),
                    Pose = new WeaponPose(f.Pose.X, f.Pose.Y, f.Pose.Degrees, f.Pose.ScaleX, f.Pose.ScaleY, f.Pose.BehindBody),
                }).ToList(),
            },
        }).ToList(),
        Effects = dto.Effects.Select(e => new EffectClip
        {
            Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id,
            Name = string.IsNullOrWhiteSpace(e.Name) ? "Effect" : e.Name,
            ImagePath = ResolveAsset(e.Image ?? string.Empty, dir),
            StartFrame = e.StartFrame,
            DurationFrames = e.DurationFrames,
            Layer = e.Layer,
            BaseTransform = FromDto(e.Transform),
            Keyframes = e.Keyframes.Select(FromDto).Where(k => k is not null).Select(k => k!).ToList(),
        }).ToList(),
        Audio = dto.Audio.Select(a => new AudioClip
        {
            Id = string.IsNullOrWhiteSpace(a.Id) ? Guid.NewGuid().ToString("N") : a.Id,
            Name = string.IsNullOrWhiteSpace(a.Name) ? "Audio" : a.Name,
            AudioPath = ResolveAsset(a.File ?? string.Empty, dir),
            StartSeconds = a.StartSeconds,
            TrimStartSeconds = a.TrimStartSeconds,
            DurationSeconds = a.DurationSeconds,
            Volume = a.Volume,
            Muted = a.Muted,
        }).ToList(),
    };

    private static Transform FromDto(TransformDto? d) => d is null ? Transform.Identity : new Transform
    {
        X = d.X, Y = d.Y, ScaleX = d.ScaleX, ScaleY = d.ScaleY,
        RotationDegrees = d.RotationDegrees, Opacity = d.Opacity,
        PivotX = d.PivotX, PivotY = d.PivotY, FlipH = d.FlipH, FlipV = d.FlipV,
    };

    private static TransformDto ToDto(Transform t) => new()
    {
        X = t.X, Y = t.Y, ScaleX = t.ScaleX, ScaleY = t.ScaleY,
        RotationDegrees = t.RotationDegrees, Opacity = t.Opacity,
        PivotX = t.PivotX, PivotY = t.PivotY, FlipH = t.FlipH, FlipV = t.FlipV,
    };

    private static TransformKeyframe? FromDto(KeyframeDto k)
    {
        if (!Enum.TryParse<TransformProperty>(k.Property, ignoreCase: true, out var prop))
            return null; // forward-compatible: skip unknown properties
        if (!Enum.TryParse<KeyframeInterpolation>(k.Interpolation, ignoreCase: true, out var interp))
            interp = KeyframeInterpolation.Hold;
        return new TransformKeyframe(prop, k.Frame, k.Value, interp);
    }

    private static KeyframeDto ToDto(TransformKeyframe k) => new()
    {
        Property = k.Property.ToString(),
        Frame = k.Frame,
        Value = k.Value,
        Interpolation = k.Interpolation.ToString(),
    };

    /// <summary>
    /// Structural validation (timing, clip shape, asset presence is checked
    /// separately). Animation-ID existence is checked via
    /// <see cref="ValidateAnimations(SequenceProject, Func{string, bool})"/>.
    /// </summary>
    public static ProjectValidation Validate(SequenceProject project)
    {
        ValidateStructure(project, out var errors);
        return new ProjectValidation(errors, Array.Empty<string>());
    }

    public static ProjectValidation ValidateAnimations(SequenceProject project, Func<string, bool> exists)
    {
        var missing = project.Clips
            .SelectMany(c => new[] { c.AnimationId }
                .Concat(c.Weapon is { Enabled: true } && !string.IsNullOrWhiteSpace(c.Weapon.DonorAnimationId)
                    ? new[] { c.Weapon.DonorAnimationId } : Array.Empty<string>()))
            .Where(id => !string.IsNullOrWhiteSpace(id) && !exists(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ProjectValidation(Array.Empty<string>(), missing);
    }

    private static void ValidateStructure(SequenceProject project, out List<string> errors)
    {
        errors = new List<string>();
        if (project.Version != SequenceProject.CurrentVersion)
            errors.Add($"unsupported version {project.Version}");
        if (project.Fps <= 0 || double.IsNaN(project.Fps) || double.IsInfinity(project.Fps))
            errors.Add("fps must be a positive finite number");
        if (string.IsNullOrWhiteSpace(project.Name))
            errors.Add("name must not be empty");
        if (project.CanvasWidth < 0 || project.CanvasHeight < 0)
            errors.Add("canvas size must not be negative");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            if (string.IsNullOrWhiteSpace(clip.AnimationId))
                errors.Add($"clip {i}: missing animation id");
            if (clip.Speed <= 0 || double.IsNaN(clip.Speed) || double.IsInfinity(clip.Speed))
                errors.Add($"clip {i} ('{clip.AnimationId}'): speed must be a positive finite number");
            CheckId(errors, ids, clip.Id, $"clip {i}");
            ValidateKeys(clip.Keyframes, $"clip {i}", errors);
            ValidateTransform(clip.BaseTransform, $"clip {i}", errors);
            if (clip.SourceFirst < 0)
                errors.Add($"clip {i} ('{clip.AnimationId}'): source first frame must not be negative");
            if (clip.SourceLast.HasValue && clip.SourceLast.Value < clip.SourceFirst)
                errors.Add($"clip {i} ('{clip.AnimationId}'): source last frame must not precede the first");
            if (clip.LeadHoldFrames < 0 || clip.LeadHoldFrames > SequenceClip.MaxHoldFrames)
                errors.Add($"clip {i} ('{clip.AnimationId}'): lead hold must be 0..{SequenceClip.MaxHoldFrames}");
            if (clip.TailHoldFrames < 0 || clip.TailHoldFrames > SequenceClip.MaxHoldFrames)
                errors.Add($"clip {i} ('{clip.AnimationId}'): tail hold must be 0..{SequenceClip.MaxHoldFrames}");
            ValidateWeapon(clip.Weapon, $"clip {i}", errors);
        }
        for (int i = 0; i < project.Effects.Count; i++)
        {
            var e = project.Effects[i];
            CheckId(errors, ids, e.Id, $"effect {i}");
            if (string.IsNullOrWhiteSpace(e.ImagePath))
                errors.Add($"effect {i}: missing image path");
            if (e.DurationFrames <= 0)
                errors.Add($"effect {i}: duration must be positive");
            if (e.StartFrame < 0)
                errors.Add($"effect {i}: start frame must not be negative");
            ValidateKeys(e.Keyframes, $"effect {i}", errors);
        ValidateTransform(e.BaseTransform, $"effect {i}", errors);
        }
        for (int i = 0; i < project.Audio.Count; i++)
        {
            var a = project.Audio[i];
            CheckId(errors, ids, a.Id, $"audio {i}");
            if (string.IsNullOrWhiteSpace(a.AudioPath))
                errors.Add($"audio {i}: missing file path");
            if (IsBadTime(a.StartSeconds) || a.StartSeconds < 0 || IsBadTime(a.TrimStartSeconds) || a.TrimStartSeconds < 0)
                errors.Add($"audio {i}: times must be non-negative finite numbers");
            if (a.DurationSeconds is { } dd && (dd <= 0 || double.IsNaN(dd) || double.IsInfinity(dd)))
                errors.Add($"audio {i}: duration must be a positive finite number");
            if (double.IsNaN(a.Volume) || double.IsInfinity(a.Volume) || a.Volume < 0 || a.Volume > 1)
                errors.Add($"audio {i}: volume must be 0..1");
        }
    }

    /// <summary>
    /// Clamp clip trims into the available source frames (game-version
    /// drift: a project authored against more frames than the loaded game
    /// provides). Returns the number of clips adjusted. Structural problems
    /// (negative starts, last-before-first) are load errors, not drift, and
    /// are left for <see cref="Validate"/> to reject.
    /// </summary>
    public static int ClampFrameRanges(SequenceProject project, Func<string, int> sourceCount)
    {
        int clamped = 0;
        foreach (var clip in project.Clips)
        {
            int count;
            try { count = sourceCount(clip.AnimationId); }
            catch (KeyNotFoundException) { continue; }
            if (count <= 0)
                continue;
            int first = Math.Min(clip.SourceFirst, count - 1);
            int last = Math.Min(clip.SourceLast ?? (count - 1), count - 1);
            if (last < first)
                last = first;
            if (first != clip.SourceFirst || last != (clip.SourceLast ?? (count - 1)))
            {
                clip.SourceFirst = first;
                clip.SourceLast = last;
                clamped++;
            }
        }
        return clamped;
    }

    private static bool IsBadTime(double v) => double.IsNaN(v) || double.IsInfinity(v);

    private static void CheckId(List<string> errors, HashSet<string> ids, string? id, string what)
    {
        if (string.IsNullOrWhiteSpace(id))
            errors.Add($"{what}: missing stable id");
        else if (!ids.Add(id))
            errors.Add($"{what}: duplicate id '{id}'");
    }

    private static void ValidateKeys(List<TransformKeyframe> keys, string what, List<string> errors)
    {
        foreach (var k in keys)
        {
            if (k.Frame < 0)
                errors.Add($"{what}: keyframe before frame 0");
            if (double.IsNaN(k.Value) || double.IsInfinity(k.Value))
                errors.Add($"{what}: keyframe value must be finite");
        }
    }

    private static void ValidateTransform(Transform t, string what, List<string> errors)
    {
        double[] vals = { t.X, t.Y, t.ScaleX, t.ScaleY, t.RotationDegrees, t.Opacity, t.PivotX, t.PivotY };
        foreach (double v in vals)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                errors.Add($"{what}: transform must be finite");
                return;
            }
        }
    }

    private static void ValidateWeapon(WeaponClipOverride? w, string what, List<string> errors)
    {
        if (w is null) return;
        if (string.IsNullOrWhiteSpace(w.DonorAnimationId))
            errors.Add($"{what}: weapon donor animation missing");
        if (w.DonorFrame < 0)
            errors.Add($"{what}: weapon donor frame must not be negative");
        if (w.BodyMaskGreen < 0 || w.BodyMaskGreen > 255 || w.DonorMaskGreen < 0 || w.DonorMaskGreen > 255)
            errors.Add($"{what}: weapon mask channels must be 0..255");
        if (w.BodyColorGreen < 0 || w.BodyColorGreen > 255)
            errors.Add($"{what}: weapon body color must be 0..255");
        if (!double.IsFinite(w.DonorPivotX) || !double.IsFinite(w.DonorPivotY))
            errors.Add($"{what}: weapon donor pivot must be finite");
        foreach (var f in w.Frames)
        {
            if (f.SourceFrame < 0) { errors.Add($"{what}: weapon source frame must not be negative"); break; }
            foreach (var r in f.Remove.Concat(f.Foreground).Concat(w.DonorRegion))
                if (r.Y < 0 || r.X < 0 || r.Length <= 0)
                { errors.Add($"{what}: weapon runs must be non-negative"); break; }
            var p = f.Pose;
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Degrees) ||
                !double.IsFinite(p.ScaleX) || !double.IsFinite(p.ScaleY) || p.ScaleX == 0 || p.ScaleY == 0)
            { errors.Add($"{what}: weapon pose must be finite with nonzero scale"); break; }
        }
    }
}
