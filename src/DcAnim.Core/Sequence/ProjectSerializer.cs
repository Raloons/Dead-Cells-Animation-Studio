using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcAnim.Core.Sequence;

/// <summary>Validation result for opening a project against a native catalog.</summary>
public sealed record ProjectValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> MissingAnimationIds)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Human-readable <c>.dcanim</c> JSON persistence. Projects reference native
/// animations by ID and store editing metadata only — never game artwork.
/// </summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class ProjectDto
    {
        public string Format { get; set; } = "dcanim";
        public int Version { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Fps { get; set; }
        public List<ClipDto> Clips { get; set; } = new();
    }

    private sealed class ClipDto
    {
        public string Id { get; set; } = string.Empty;
        public string Animation { get; set; } = string.Empty;
        public double Speed { get; set; }
    }

    public static void Save(SequenceProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateStructure(project, out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException("Cannot save invalid project: " + string.Join("; ", errors));

        var dto = new ProjectDto
        {
            Version = project.Version,
            Name = project.Name,
            Fps = project.Fps,
            Clips = project.Clips.Select(c => new ClipDto
            {
                Id = c.Id,
                Animation = c.AnimationId,
                Speed = c.Speed,
            }).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    public static SequenceProject Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
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
        if (!string.Equals(dto.Format, "dcanim", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{path}' is not a Dead Cells Animation Studio project (bad format tag).");
        if (dto.Version != SequenceProject.CurrentVersion)
            throw new NotSupportedException(
                $"Unsupported project version {dto.Version} (this build supports version {SequenceProject.CurrentVersion}).");

        var project = new SequenceProject
        {
            Version = dto.Version,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? Path.GetFileNameWithoutExtension(path) : dto.Name,
            Fps = dto.Fps,
            Clips = dto.Clips.Select(c => new SequenceClip
            {
                Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id,
                AnimationId = c.Animation ?? string.Empty,
                Speed = c.Speed,
            }).ToList(),
        };

        ValidateStructure(project, out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"Project file '{path}' failed validation: " + string.Join("; ", errors));
        return project;
    }

    /// <summary>
    /// Structural validation (timing values, clip shape). Animation-ID
    /// existence is checked separately via
    /// <see cref="ValidateAnimations(SequenceProject, Func{string, bool})"/>
    /// because the catalog lives outside the file.
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
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            if (string.IsNullOrWhiteSpace(clip.AnimationId))
                errors.Add($"clip {i}: missing animation id");
            if (clip.Speed <= 0 || double.IsNaN(clip.Speed) || double.IsInfinity(clip.Speed))
                errors.Add($"clip {i} ('{clip.AnimationId}'): speed must be a positive finite number");
            if (string.IsNullOrWhiteSpace(clip.Id))
                errors.Add($"clip {i}: missing stable id");
            else if (!ids.Add(clip.Id))
                errors.Add($"clip {i}: duplicate clip id '{clip.Id}'");
        }
    }
}
