using DcAnim.Core.Model;

namespace DcAnim.Core.Export;

/// <summary>
/// PNG frame-sequence exporter. One transparent full-canvas PNG per output
/// frame, deterministic names (<c>frame_0000.png</c>…), pixel-identical to
/// the editor preview (same composed frames, checkerboard never included).
/// Refuses to write into a non-empty directory unless
/// <paramref name="overwrite"/> is set — never silently overwrites.
/// </summary>
public static class PngSequenceExporter
{
    public static IReadOnlyList<string> Export(
        IReadOnlyList<ClipFrame> frames,
        string outputDirectory,
        bool overwrite = false,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (w, h) = ExportCanvas.Measure(frames);
        Directory.CreateDirectory(outputDirectory);
        var existing = Directory.GetFiles(outputDirectory, "frame_*.png");
        if (existing.Length > 0 && !overwrite)
            throw new IOException(
                $"Output directory '{outputDirectory}' already contains {existing.Length} frame_*.png file(s). " +
                "Choose an empty folder or enable overwrite.");

        var written = new List<string>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rgba = ExportCanvas.ComposeCentered(frames[i], w, h);
            using var bmp = ExportCanvas.ToSkBitmap(rgba, w, h);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            string path = Path.Combine(outputDirectory, $"frame_{i:0000}.png");
            File.WriteAllBytes(path, data.ToArray());
            written.Add(path);
            progress?.Report(new ExportProgress((i + 1) / (double)frames.Count, $"Frame {i + 1} / {frames.Count}"));
        }
        return written;
    }
}
