using DcAnim.Core.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace DcAnim.Core.Export;

/// <summary>
/// Animated GIF exporter.
/// <para/>
/// Honest GIF constraints (documented, not hidden):
/// <list type="bullet">
/// <item>Timing is quantized to 10 ms units: each frame delay is
/// <c>round(1000 / fps / 10) · 10 ms</c>. Reported back via
/// <see cref="GifExportInfo"/> so callers can show effective FPS.</item>
/// <item>Colors are quantized to a 256-color palette (ImageSharp octree
/// quantizer, no dithering by default to preserve flat pixel-art areas).
/// Do not expect bit-identical colors on gradients.</item>
/// <item>Transparency is 1-bit: fully transparent pixels stay transparent;
/// semi-transparent edges snap to opaque/transparent.</item>
/// </list>
/// </summary>
public static class GifExporter
{
    public sealed record GifExportInfo(
        string Path,
        int FrameCount,
        int FrameDelayCentiseconds,
        double EffectiveFps,
        bool Loop);

    public static GifExportInfo Export(
        IReadOnlyList<ClipFrame> frames,
        double fps,
        string outputPath,
        bool loop = true,
        bool overwrite = false,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
        var (w, h) = ExportCanvas.Measure(frames);
        if (File.Exists(outputPath) && !overwrite)
            throw new IOException($"'{outputPath}' already exists. Enable overwrite or choose another file.");

        int delayCs = Math.Max(1, (int)Math.Round(1000.0 / fps / 10.0, MidpointRounding.AwayFromZero));
        double effectiveFps = 100.0 / delayCs;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        Image<Rgba32>? gif = null;
        try
        {
            for (int i = 0; i < frames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rgba = ExportCanvas.ComposeCentered(frames[i], w, h);
                using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
                img.Mutate(ctx => ctx.Quantize(new OctreeQuantizer(
                    new QuantizerOptions { MaxColors = 256, Dither = null })));
                img.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCs;
                img.Frames.RootFrame.Metadata.GetGifMetadata().DisposalMethod =
                    SixLabors.ImageSharp.Formats.Gif.GifDisposalMethod.RestoreToBackground;
                if (gif is null)
                    gif = img.Clone(); // take ownership of the first frame
                else
                    gif.Frames.AddFrame(img.Frames.RootFrame);
                progress?.Report(new ExportProgress((i + 1) / (double)frames.Count, $"Frame {i + 1} / {frames.Count}"));
            }

            gif!.Metadata.GetGifMetadata().RepeatCount = loop ? (ushort)0 : (ushort)1;
            gif.SaveAsGif(outputPath);
        }
        finally
        {
            gif?.Dispose();
        }
        return new GifExportInfo(outputPath, frames.Count, delayCs, effectiveFps, loop);
    }
}
