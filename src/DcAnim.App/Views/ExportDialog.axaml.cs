using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace DcAnim.App.Views;

public enum ExportKind { PngSequence, Gif, SpriteSheet }

public sealed record ExportSettings(
    ExportKind Kind,
    string Destination,
    bool GifLoop,
    int SheetColumns,
    int SheetPadding,
    bool Overwrite);

/// <summary>Compact export dialog: format, destination, options, estimate, progress.</summary>
public sealed partial class ExportDialog : Window
{
    private readonly TaskCompletionSource<ExportSettings?> _result = new();

    public ExportDialog(ExportKind kind, int frameCount, double fps)
    {
        InitializeComponent();
        PngOption.IsChecked = kind == ExportKind.PngSequence;
        GifOption.IsChecked = kind == ExportKind.Gif;
        SheetOption.IsChecked = kind == ExportKind.SpriteSheet;
        EstimateText.Text = $"{frameCount} frames · {frameCount / fps:F3} s @ {fps:0.#} FPS" +
            (kind == ExportKind.Gif ? $" · GIF step {GifStepMs(fps):0} ms" : string.Empty);
        UpdateOptionVisibility();
        PngOption.IsCheckedChanged += (_, _) => UpdateOptionVisibility();
        GifOption.IsCheckedChanged += (_, _) => UpdateOptionVisibility();
        SheetOption.IsCheckedChanged += (_, _) => UpdateOptionVisibility();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        ExportButton.Click += (_, _) => Finish();
        CancelButton.Click += (_, _) => { _result.TrySetResult(null); Close(); };
    }

    private static double GifStepMs(double fps) => Math.Round(1000.0 / fps / 10.0) * 10.0;

    public Task<ExportSettings?> WaitForSettingsAsync() => _result.Task;

    private ExportKind Kind =>
        GifOption.IsChecked == true ? ExportKind.Gif :
        SheetOption.IsChecked == true ? ExportKind.SpriteSheet : ExportKind.PngSequence;

    private void UpdateOptionVisibility()
    {
        LoopCheck.IsEnabled = Kind == ExportKind.Gif;
        ColumnsBox.IsEnabled = Kind == ExportKind.SpriteSheet;
        PaddingBox.IsEnabled = Kind == ExportKind.SpriteSheet;
        DestinationBox.Watermark = Kind == ExportKind.PngSequence
            ? "Output folder…"
            : Kind == ExportKind.Gif ? "Output .gif file…" : "Output sheet .png file…";
    }

    private async Task BrowseAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;
        if (Kind == ExportKind.PngSequence)
        {
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder for PNG frames",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
                DestinationBox.Text = folders[0].Path.LocalPath;
        }
        else
        {
            string pattern = Kind == ExportKind.Gif ? "*.gif" : "*.png";
            string title = Kind == ExportKind.Gif ? "Save animated GIF" : "Save sprite sheet PNG";
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = Kind == ExportKind.Gif ? "sequence.gif" : "sheet.png",
                FileTypeChoices = new[] { new FilePickerFileType("Image") { Patterns = new[] { pattern } } },
            });
            if (file is not null)
                DestinationBox.Text = file.Path.LocalPath;
        }
    }

    private void Finish()
    {
        if (string.IsNullOrWhiteSpace(DestinationBox.Text))
        {
            ResultText.Text = "Choose a destination first.";
            return;
        }
        _result.TrySetResult(new ExportSettings(
            Kind,
            DestinationBox.Text.Trim(),
            LoopCheck.IsChecked ?? true,
            (int)(ColumnsBox.Value ?? 0),
            (int)(PaddingBox.Value ?? 0),
            OverwriteCheck.IsChecked ?? false));
    }

    public void ReportProgress(double fraction, string message)
    {
        ProgressBar.IsVisible = true;
        ProgressBar.Value = fraction;
        ResultText.Text = message;
    }

    public void ReportDone(string message)
    {
        ProgressBar.Value = 1;
        ResultText.Text = message;
    }

    public void ReportError(string message) => ResultText.Text = message;
}
