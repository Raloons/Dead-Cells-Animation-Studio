namespace DcAnim.Core.Atlas;

/// <summary>A single packed sprite region inside a BATL atlas page.</summary>
/// <param name="Name">Region name, e.g. <c>AtkKatanaB2_00</c>.</param>
/// <param name="Page">Zero-based page index into <see cref="BatlAtlasData.Pages"/>.</param>
/// <param name="X">Left pixel in the page image.</param>
/// <param name="Y">Top pixel in the page image.</param>
/// <param name="Width">Trimmed sprite width.</param>
/// <param name="Height">Trimmed sprite height.</param>
/// <param name="OffsetX">Left paste position on the original-size canvas.</param>
/// <param name="OffsetY">Top paste position on the original-size canvas.</param>
/// <param name="OriginalWidth">Full (untrimmed) frame width.</param>
/// <param name="OriginalHeight">Full (untrimmed) frame height.</param>
public sealed record BatlRegion(
    string Name,
    int Page,
    int X, int Y,
    int Width, int Height,
    int OffsetX, int OffsetY,
    int OriginalWidth, int OriginalHeight);

/// <summary>Parsed BATL atlas: page image names plus sprite regions.</summary>
public sealed record BatlAtlasData(
    IReadOnlyList<string> Pages,
    IReadOnlyList<BatlRegion> Regions,
    int DroppedTrailingRegions);
