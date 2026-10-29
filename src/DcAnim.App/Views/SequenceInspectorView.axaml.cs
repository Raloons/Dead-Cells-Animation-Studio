using Avalonia.Controls;

namespace DcAnim.App.Views;

/// <summary>Right panel in sequence mode: selected clip timing + project settings.</summary>
public sealed partial class SequenceInspectorView : UserControl
{
    public event Action<double>? ClipSpeedChanged;
    public event Action<double>? ProjectFpsChanged;
    public event Action<string>? ProjectRenamed;

    private bool _syncing;

    public SequenceInspectorView()
    {
        InitializeComponent();
        ClipSpeed.ValueChanged += (_, _) =>
        {
            if (_syncing || ClipSpeed.Value is not { } v)
                return;
            ClipSpeedChanged?.Invoke((double)v);
        };
        ClipSpeedPreset.SelectionChanged += (_, _) =>
        {
            if (_syncing || ClipSpeedPreset.SelectedItem is not ComboBoxItem item ||
                !double.TryParse(item.Tag?.ToString(), out double v))
                return;
            ClipSpeedChanged?.Invoke(v);
        };
        ProjectFps.ValueChanged += (_, _) =>
        {
            if (_syncing || ProjectFps.Value is not { } v)
                return;
            ProjectFpsChanged?.Invoke((double)v);
        };
        ProjectName.LostFocus += (_, _) =>
        {
            if (!_syncing && !string.IsNullOrWhiteSpace(ProjectName.Text))
                ProjectRenamed?.Invoke(ProjectName.Text.Trim());
        };
        Clear();
    }

    public void Clear()
    {
        _syncing = true;
        try
        {
            ClipAnim.Text = "Select a clip on the timeline";
            ClipFrames.Text = "—";
            ClipDuration.Text = "—";
            SeqClips.Text = "—";
            SeqTotal.Text = "—";
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowClip(string animationId, double speed, int outputFrames, double fps)
    {
        _syncing = true;
        try
        {
            ClipAnim.Text = animationId;
            ClipSpeed.Value = (decimal)speed;
            SyncPreset(speed);
            ClipFrames.Text = outputFrames.ToString();
            ClipDuration.Text = $"{outputFrames / fps:F3} s";
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowProject(string name, double fps, int clipCount, int totalFrames)
    {
        _syncing = true;
        try
        {
            if (ProjectName.Text != name)
                ProjectName.Text = name;
            ProjectFps.Value = (decimal)fps;
            SeqClips.Text = clipCount.ToString();
            SeqTotal.Text = $"{totalFrames} frames · {totalFrames / fps:F3} s";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncPreset(double speed)
    {
        foreach (var item in ClipSpeedPreset.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() is { } tag && double.TryParse(tag, out double v) &&
                Math.Abs(v - speed) < 1e-9)
            {
                ClipSpeedPreset.SelectedItem = item;
                return;
            }
        }
        ClipSpeedPreset.SelectedItem = null;
    }
}
