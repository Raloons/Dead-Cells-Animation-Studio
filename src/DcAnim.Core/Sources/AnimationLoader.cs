using System.Text.Json;
using System.Text.RegularExpressions;
using DcAnim.Core.Atlas;
using DcAnim.Core.Catalog;
using DcAnim.Core.Model;
using DcAnim.Core.Pak;

namespace DcAnim.Core.Sources;

/// <summary>
/// Loads animation clips either straight from a <c>res.pak</c> (no extraction
/// needed) or from an extracted directory containing <c>&lt;name&gt;.atlas</c>
/// plus page PNGs. No game files are copied into the application.
/// </summary>
public sealed class AnimationLoader : IDisposable
{
    private readonly BatlAtlasData _atlas;
    private readonly AtlasFrameComposer _composer;
    private readonly AnimationCatalog _catalog;
    private readonly string _sourceDescription;
    private readonly Dictionary<string, BatlRegion> _regionsByName;
    private readonly IReadOnlyDictionary<string, AnimationTiming>? _timing;
    private bool _disposed;

    public AnimationCatalog Catalog => _catalog;
    public string SourceDescription => _sourceDescription;

    private AnimationLoader(BatlAtlasData atlas, AtlasFrameComposer composer, string sourceDescription, IReadOnlyDictionary<string, AnimationTiming>? timing)
    {
        _atlas = atlas;
        _composer = composer;
        _sourceDescription = sourceDescription;
        _timing = timing;
        _catalog = new AnimationCatalog(atlas.Regions.Select(r => r.Name), timing);
        _regionsByName = atlas.Regions.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    public static AnimationLoader FromResPak(string resPakPath, string atlasEntryPath = "atlas/beheaded.atlas", IReadOnlyDictionary<string, AnimationTiming>? timing = null)
    {
        using var pak = PakReader.Open(resPakPath);
        if (!pak.Contains(atlasEntryPath))
            throw new FileNotFoundException($"Atlas '{atlasEntryPath}' not found in '{resPakPath}'.");
        var atlasBytes = pak.ReadFile(atlasEntryPath);
        var atlas = BatlParser.Parse(atlasBytes);

        string dir = atlasEntryPath.Contains('/') ? atlasEntryPath[..atlasEntryPath.LastIndexOf('/')] : string.Empty;
        var pagePngs = new List<byte[]>();
        foreach (var page in atlas.Pages)
        {
            string entry = string.IsNullOrEmpty(dir) ? page : dir + "/" + page;
            if (!pak.Contains(entry))
                throw new FileNotFoundException($"Atlas page '{entry}' not found in '{resPakPath}'.");
            pagePngs.Add(pak.ReadFile(entry));
        }

        string desc = $"res.pak :: {atlasEntryPath} ({atlas.Regions.Count} regions, {atlas.Pages.Count} pages)";
        return new AnimationLoader(atlas, new AtlasFrameComposer(pagePngs), desc, timing);
    }

    public static AnimationLoader FromDirectory(string directory, string atlasFileName = "beheaded.atlas", IReadOnlyDictionary<string, AnimationTiming>? timing = null)
    {
        string atlasPath = Path.Combine(directory, atlasFileName);
        if (!File.Exists(atlasPath))
            throw new FileNotFoundException($"Atlas file not found: '{atlasPath}'.");
        var atlas = BatlParser.ParseFile(atlasPath);

        var pagePngs = new List<byte[]>();
        foreach (var page in atlas.Pages)
        {
            string pagePath = Path.Combine(directory, page);
            if (!File.Exists(pagePath))
                throw new FileNotFoundException($"Atlas page not found: '{pagePath}'.");
            pagePngs.Add(File.ReadAllBytes(pagePath));
        }

        string desc = $"{directory} :: {atlasFileName} ({atlas.Regions.Count} regions, {atlas.Pages.Count} pages)";
        return new AnimationLoader(atlas, new AtlasFrameComposer(pagePngs), desc, timing);
    }

    public AnimationClip LoadClip(string animationId, double fps = 30.0, IReadOnlyList<TimeSpan>? perFrameDurations = null)
    {
        var regions = _catalog.GetFrameRegions(animationId);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (perFrameDurations is not null && perFrameDurations.Count != regions.Count)
            throw new ArgumentException("Per-frame duration count must match frame count.", nameof(perFrameDurations));

        var frames = new List<ClipFrame>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
        {
            var duration = perFrameDurations?[i] ?? TimeSpan.FromSeconds(1.0 / fps);
            frames.Add(ComposeFrame(animationId, regions[i], i, duration));
        }

        AnimationTiming? t = null;
        _timing?.TryGetValue(animationId, out t);
        return new AnimationClip
        {
            Id = animationId,
            Frames = frames,
            SourceDescription = _sourceDescription,
            Timing = t,
            DefaultFps = fps,
        };
    }

    /// <summary>
    /// Decodes a single frame (e.g. frame 0 for library thumbnails) without
    /// composing the whole clip. Throws <see cref="KeyNotFoundException"/> for
    /// unknown IDs and <see cref="ArgumentOutOfRangeException"/> for bad indexes.
    /// </summary>
    public ClipFrame LoadFrame(string animationId, int frameIndex, double fps = 30.0)
    {
        var regions = _catalog.GetFrameRegions(animationId);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (frameIndex < 0 || frameIndex >= regions.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return ComposeFrame(animationId, regions[frameIndex], frameIndex, TimeSpan.FromSeconds(1.0 / fps));
    }

    private ClipFrame ComposeFrame(string animationId, string regionName, int frameIndex, TimeSpan duration)
    {
        if (!_regionsByName.TryGetValue(regionName, out var region))
            throw new InvalidDataException($"Catalog region '{regionName}' of '{animationId}' has no atlas entry.");
        return _composer.Compose(region, frameIndex, duration);
    }

    /// <summary>Loads a small hand-authored timing overlay (animId → hitFrame/charge/...). No game art involved.</summary>
    public static IReadOnlyDictionary<string, AnimationTiming> LoadTimingOverlay(string jsonPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var map = new Dictionary<string, AnimationTiming>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var el = prop.Value;
            if (el.ValueKind != JsonValueKind.Object)
                continue; // e.g. "_note" documentation string
            map[prop.Name] = new AnimationTiming(
                HitFrame: el.TryGetProperty("hitFrame", out var h) ? h.GetInt32() : null,
                ChargeSeconds: el.TryGetProperty("charge", out var c) ? c.GetDouble() : null,
                AnimSpeed: el.TryGetProperty("animSpd", out var s) ? s.GetDouble() : null,
                FxId: el.TryGetProperty("fxId", out var f) ? f.GetString() : null,
                SourceNote: el.TryGetProperty("source", out var n) ? n.GetString() : null);
        }
        return map;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _composer.Dispose();
    }
}
