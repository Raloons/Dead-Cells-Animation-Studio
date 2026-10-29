using System.IO;
using Avalonia.Controls;

namespace DcAnim.App;

/// <summary>
/// Last-resort crash reporting. All known workflows are guarded at their own
/// level; this only ensures that anything unforeseen still leaves a trace
/// (log file) instead of vanishing silently.
/// </summary>
public static class CrashLog
{
    public static string? LastPath { get; private set; }

    public static void Write(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DeadCellsAnimationStudio");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}");
            LastPath = path;
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public static async Task ShowDialogAsync(Window owner, string logPath)
    {
        try
        {
            var text = new TextBlock
            {
                Text = "Something unexpected went wrong, but the studio kept running.\n\n" +
                       $"Details were written to:{Environment.NewLine}{logPath}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            var ok = new Button { Content = "OK", MinWidth = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(16) };
            panel.Children.Add(text);
            panel.Children.Add(ok);
            var dialog = new Window
            {
                Title = "Unexpected error",
                Width = 440,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel,
            };
            ok.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(owner);
        }
        catch
        {
            // Never fail while reporting failure.
        }
    }
}
