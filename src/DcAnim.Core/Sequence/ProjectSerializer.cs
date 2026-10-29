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
/// (AS-M3) load via <see cref="MigrateV1ToV2"/> with documented defaults.
/// Asset references stay as paths (relative to the project file when
/// practical) — never embedded artwork.
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
    }

    private sealed class ClipDto
    {
        public string Id { get; set; } = string.Empty;
        public string Animation { get; set; } = string.Empty;
        public double Speed { get; set; } = 1.0;
        public TransformDto? Transform { get; set; }
        public List<KeyframeDto> Keyframes { get; set; } = new();
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
        };
        Directory.CreateDirectory(dir!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
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
        if (dto.Version != 1 && dto.Version != SequenceProject.CurrentVersion)
            throw new NotSupportedException(
                $"Unsupported project version {dto.Version} (this build supports versions 1 and {SequenceProject.CurrentVersion}).");

        var project = FromDto(dto, dir);
        ValidateStructure(project, out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"Project file '{path}' failed validation: " + string.Join("; ", errors));
        return project;
    }

    /// <summary>v1 → v2: identity transform, no keyframes, no tracks.</summary>
    public static SequenceProject MigrateV1ToV2(SequenceProject v1) =>
        new()
        {
            Version = SequenceProject.CurrentVersion,
            Name = v1.Name,
            Fps = v1.Fps,
            Clips = v1.Clips.Select(c => new SequenceClip
            {
                Id = c.Id,
                AnimationId = c.AnimationId,
                Speed = c.Speed,
                BaseTransform = Transform.Identity,
                Keyframes = new List<TransformKeyframe>(),
            }).ToList(),
        };

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
        CanvasWidth = Math.Max(0, dto.CanvasWidth),
        CanvasHeight = Math.Max(0, dto.CanvasHeight),
        Clips = dto.Clips.Select(c => new SequenceClip
        {
            Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id,
            AnimationId = c.Animation ?? string.Empty,
            Speed = c.Speed <= 0 ? 1.0 : c.Speed,
            BaseTransform = FromDto(c.Transform),
            Keyframes = c.Keyframes.Select(FromDto).Where(k => k is not null).Select(k => k!).ToList(),
        }).ToList(),
        Effects = dto.Effects.Select(e => new EffectClip
        {
            Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id,
            Name = string.IsNullOrWhiteSpace(e.Name) ? "Effect" : e.Name,
            ImagePath = ResolveAsset(e.Image ?? string.Empty, dir),
            StartFrame = Math.Max(0, e.StartFrame),
            DurationFrames = Math.Max(1, e.DurationFrames),
            Layer = e.Layer,
            BaseTransform = FromDto(e.Transform),
            Keyframes = e.Keyframes.Select(FromDto).Where(k => k is not null).Select(k => k!).ToList(),
        }).ToList(),
        Audio = dto.Audio.Select(a => new AudioClip
        {
            Id = string.IsNullOrWhiteSpace(a.Id) ? Guid.NewGuid().ToString("N") : a.Id,
            Name = string.IsNullOrWhiteSpace(a.Name) ? "Audio" : a.Name,
            AudioPath = ResolveAsset(a.File ?? string.Empty, dir),
            StartSeconds = Math.Max(0, a.StartSeconds),
            TrimStartSeconds = Math.Max(0, a.TrimStartSeconds),
            DurationSeconds = a.DurationSeconds is > 0 ? a.DurationSeconds : null,
            Volume = Math.Clamp(a.Volume, 0.0, 1.0),
            Muted = a.Muted,
        }).ToList(),
    };

    private static Transform FromDto(TransformDto? d) => d is null ? Transform.Identity : new Transform
    {
        X = d.X, Y = d.Y, ScaleX = d.ScaleX, ScaleY = d.ScaleY,
        RotationDegrees = d.RotationDegrees, Opacity = Math.Clamp(d.Opacity, 0, 1),
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
        if (k.Frame < 0)
            return null;
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
            .Select(c => c.AnimationId)
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
        }
        for (int i = 0; i < project.Audio.Count; i++)
        {
            var a = project.Audio[i];
            CheckId(errors, ids, a.Id, $"audio {i}");
            if (string.IsNullOrWhiteSpace(a.AudioPath))
                errors.Add($"audio {i}: missing file path");
            if (a.StartSeconds < 0 || a.TrimStartSeconds < 0)
                errors.Add($"audio {i}: times must not be negative");
            if (a.DurationSeconds is <= 0)
                errors.Add($"audio {i}: duration must be positive");
            if (a.Volume < 0 || a.Volume > 1)
                errors.Add($"audio {i}: volume must be 0..1");
        }
    }

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
}
