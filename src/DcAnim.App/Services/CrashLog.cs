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

    // Storm guard: a per-frame failure (e.g. during playback) must never
    // stack modal dialogs faster than the user can dismiss them.
    private static readonly object _gate = new();
    private static bool _showing;
    private static DateTime _lastShownUtc = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    public static void Write(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DeadCellsAnimationStudio");
            Directory.CreateDirectory(dir);
            // Millisecond resolution: per-frame storms must not overwrite
            // each other's logs.
            string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}");
            LastPath = path;
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>
    /// Returns false when the dialog was suppressed by the storm guard
    /// (already open, or shown within the cooldown). The log file is always
    /// written regardless — use <see cref="LastPath"/> for the latest one.
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, string logPath)
    {
        lock (_gate)
        {
            if (_showing || DateTime.UtcNow - _lastShownUtc < Cooldown)
                return false;
            _showing = true;
            _lastShownUtc = DateTime.UtcNow;
        }
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
            return true;
        }
        catch
        {
            // Never fail while reporting failure.
            return false;
        }
        finally
        {
            lock (_gate) { _showing = false; }
        }
    }
}
