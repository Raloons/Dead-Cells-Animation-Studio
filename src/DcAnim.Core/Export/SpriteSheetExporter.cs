using System.Text.Json;
using DcAnim.Core.Model;
using DcAnim.Core.Sequence;

namespace DcAnim.Core.Export;

/// <summary>
/// Sprite-sheet exporter: transparent PNG atlas + JSON metadata. Layout is a
/// plain row-major grid (configurable columns, optional padding); every cell
/// is the full sequence canvas so all frames share coordinates with preview
/// and PNG export. No power-of-two restriction. This is a generic art-tool
/// interchange sheet — not Dead Cells' runtime BATL format.
/// </summary>
public static class SpriteSheetExporter
{
    public sealed record SheetFrame(
        int Index,
        int X, int Y,
        int Width, int Height,
        string AnimationId,
        int ClipIndex,
        int SourceFrame);

    public sealed record SheetInfo(
        string Image,
        int ImageWidth, int ImageHeight,
        int Columns, int Rows,
        int CellWidth, int CellHeight,
        int Padding,
        double Fps,
        double FrameDurationSeconds,
        IReadOnlyList<SheetFrame> Frames,
        IReadOnlyList<ClipBoundary> Clips);

    public sealed record ClipBoundary(
        int ClipIndex,
        string ClipId,
        string AnimationId,
        double Speed,
        int StartFrame,
        int FrameCount);

    public static SheetInfo Export(
        IReadOnlyList<ClipFrame> frames,
        IReadOnlyList<ResolvedClip> resolved,
        double fps,
        string imagePath,
        string metadataPath,
        int columns = 0,
        int padding = 0,
        bool overwrite = false,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (columns < 0)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding));
        var (cellW, cellH) = ExportCanvas.Measure(frames);
        if (File.Exists(imagePath) && !overwrite)
            throw new IOException($"'{imagePath}' already exists. Enable overwrite or choose another file.");

        int cols = columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(frames.Count));
        cols = Math.Max(1, cols);
        int rows = (frames.Count + cols - 1) / cols;
        int sheetW = cols * cellW + (cols + 1) * padding;
        int sheetH = rows * cellH + (rows + 1) * padding;

        using var surface = SkiaSharp.SKSurface.Create(
            new SkiaSharp.SKImageInfo(sheetW, sheetH, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);

        var sheetFrames = new List<SheetFrame>(frames.Count);
        // Source frame index within each animation for metadata.
        var perClipCursor = resolved.ToDictionary(r => r.ClipIndex, _ => 0);
        var clipOfGlobal = new int[frames.Count];
        {
            int g = 0;
            foreach (var r in resolved)
                for (int k = 0; k < r.OutputFrames; k++)
                    clipOfGlobal[g++] = r.ClipIndex;
        }

        for (int i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cx = i % cols, cy = i / cols;
            int x = padding + cx * (cellW + padding);
            int y = padding + cy * (cellH + padding);
            var rgba = ExportCanvas.ComposeCentered(frames[i], cellW, cellH);
            using var bmp = ExportCanvas.ToSkBitmap(rgba, cellW, cellH);
            surface.Canvas.DrawBitmap(bmp, x, y);

            int clipIndex = clipOfGlobal[i];
            var r = resolved.First(rr => rr.ClipIndex == clipIndex);
            int src = SequenceTiming.SourceIndex(
                perClipCursor[clipIndex]++, r.SourceFrames, r.OutputFrames);
            sheetFrames.Add(new SheetFrame(i, x, y, cellW, cellH, r.Clip.AnimationId, clipIndex, src));
            progress?.Report(new ExportProgress((i + 1) / (double)frames.Count, $"Frame {i + 1} / {frames.Count}"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
        using (var img = surface.Snapshot())
        using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(imagePath, data.ToArray());

        var info = new SheetInfo(
            Path.GetFileName(imagePath), sheetW, sheetH, cols, rows, cellW, cellH, padding,
            fps, 1.0 / fps, sheetFrames,
            resolved.Select(r => new ClipBoundary(r.ClipIndex, r.Clip.Id, r.Clip.AnimationId, r.Clip.Speed, r.StartFrame, r.OutputFrames)).ToList());

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metadataPath))!);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(info,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return info;
    }
}
