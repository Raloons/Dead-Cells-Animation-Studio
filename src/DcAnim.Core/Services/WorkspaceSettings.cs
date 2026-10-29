using System.Text.Json;

namespace DcAnim.Core.Services;

/// <summary>
/// Persists workspace dimensions (panel widths, timeline height, window size)
/// to a per-user JSON file. Paths are resolved from the user profile — never
/// hardcoded — and all failures degrade to defaults.
/// </summary>
public sealed class WorkspaceSettings
{
    public double LeftPanelWidth { get; set; } = 300;
    public double RightPanelWidth { get; set; } = 300;
    public double TimelineHeight { get; set; } = 230;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    public double ClampedLeftWidth => Math.Clamp(LeftPanelWidth, 200, 560);
    public double ClampedRightWidth => Math.Clamp(RightPanelWidth, 220, 560);
    public double ClampedTimelineHeight => Math.Clamp(TimelineHeight, 140, 480);

    public static string DefaultPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "DeadCellsAnimationStudio", "workspace.json");
        }
    }

    public static WorkspaceSettings Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
                return new WorkspaceSettings();
            var settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(file));
            return settings ?? new WorkspaceSettings();
        }
        catch
        {
            return new WorkspaceSettings();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Clamp before persisting so a bad session cannot wedge the layout.
            LeftPanelWidth = ClampedLeftWidth;
            RightPanelWidth = ClampedRightWidth;
            TimelineHeight = ClampedTimelineHeight;
            File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are best-effort; never break the app over them.
        }
    }
}
