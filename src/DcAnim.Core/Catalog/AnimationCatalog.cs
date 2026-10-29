using System.Text.RegularExpressions;
using DcAnim.Core.Model;

namespace DcAnim.Core.Catalog;

/// <summary>Lightweight descriptor for one animation found in an atlas.</summary>
public sealed record AnimationDescriptor(
    string Id,
    int FrameCount,
    int Width,
    int Height,
    int? HitFrame,
    string? SourceNote);

/// <summary>
/// Groups BATL regions into animations by the <c>AnimName_NN</c> convention:
/// region names ending in <c>_NN</c> (digits) belong to animation <c>AnimName</c>,
/// ordered numerically. Regions without a numeric suffix are listed as
/// single-frame entries. Numbering gaps are reported, not hidden.
/// </summary>
public sealed class AnimationCatalog
{
    private static readonly Regex FrameSuffix = new(@"^(.*)_(\d+)$", RegexOptions.Compiled);

    private readonly List<AnimationDescriptor> _animations;
    private readonly Dictionary<string, List<string>> _framesById;

    public AnimationCatalog(IEnumerable<string> regionNames, IReadOnlyDictionary<string, AnimationTiming>? timing = null)
    {
        var groups = new Dictionary<string, List<(int Number, string Region)>>(StringComparer.Ordinal);
        var singles = new List<string>();

        foreach (var region in regionNames)
        {
            var m = FrameSuffix.Match(region);
            if (m.Success && int.TryParse(m.Groups[2].Value, out int n))
            {
                string id = m.Groups[1].Value;
                if (!groups.TryGetValue(id, out var list))
                    groups[id] = list = new List<(int, string)>();
                list.Add((n, region));
            }
            else
            {
                singles.Add(region);
            }
        }

        _animations = new List<AnimationDescriptor>(groups.Count + singles.Count);
        _framesById = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (id, frames) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            frames.Sort((a, b) => a.Number.CompareTo(b.Number));
            var ordered = frames.Select(f => f.Region).ToList();
            _framesById[id] = ordered;
            AnimationTiming? hit = timing is not null && timing.TryGetValue(id, out var found) ? found : null;
            _animations.Add(new AnimationDescriptor(id, ordered.Count, 0, 0, hit?.HitFrame, hit?.SourceNote));
        }

        foreach (var single in singles.OrderBy(s => s, StringComparer.Ordinal))
        {
            _framesById[single] = new List<string> { single };
            AnimationTiming? hit = timing is not null && timing.TryGetValue(single, out var found) ? found : null;
            _animations.Add(new AnimationDescriptor(single, 1, 0, 0, hit?.HitFrame, hit?.SourceNote));
        }
    }

    public IReadOnlyList<AnimationDescriptor> Animations => _animations;

    public int Count => _animations.Count;

    public IReadOnlyList<string> GetFrameRegions(string animationId)
    {
        if (!_framesById.TryGetValue(animationId, out var frames))
            throw new KeyNotFoundException($"Animation '{animationId}' is not in the catalog.");
        return frames;
    }

    public bool Contains(string animationId) => _framesById.ContainsKey(animationId);

    /// <summary>Case-insensitive substring search over animation IDs.</summary>
    public IEnumerable<AnimationDescriptor> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _animations;
        return _animations.Where(a => a.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns frame-number gaps (missing suffixes) for an animation, if any.</summary>
    public IReadOnlyList<int> GetNumberingGaps(string animationId)
    {
        var frames = GetFrameRegions(animationId);
        var numbers = new List<int>();
        foreach (var f in frames)
        {
            var m = FrameSuffix.Match(f);
            if (m.Success && int.TryParse(m.Groups[2].Value, out int n))
                numbers.Add(n);
        }
        if (numbers.Count == 0)
            return Array.Empty<int>();
        numbers.Sort();
        var gaps = new List<int>();
        for (int expected = numbers[0]; expected <= numbers[^1]; expected++)
            if (!numbers.Contains(expected))
                gaps.Add(expected);
        return gaps;
    }
}
