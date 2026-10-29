using Avalonia.Controls;
using Avalonia.Interactivity;
using DcAnim.Core.Sequence;

namespace DcAnim.App.Views;

public sealed record KeyRow(TransformProperty Property, int Frame, double Value, KeyframeInterpolation Interpolation)
{
    public string Label => $"{Property} @ {Frame} = {Value:0.##} ({Interpolation})";
}

/// <summary>
/// Right panel in sequence mode. Sections for the selected clip, effect or
/// audio (only the active target's section is visible) plus sequence settings.
/// Transform edits commit on focus loss; keyframes edit the selected target.
/// </summary>
public sealed partial class SequenceInspectorView : UserControl
{
    public event Action<double>? ClipSpeedChanged;
    public event Action<double>? ProjectFpsChanged;
    public event Action<string>? ProjectRenamed;
    public event Action<Transform>? ClipTransformCommitted;
    public event Action<TransformProperty>? KeyAddRequested;
    public event Action<TransformProperty, int>? KeyDeleteRequested;
    public event Action<TransformProperty, int>? KeySelected;
    public event Action? KeyPrevRequested;
    public event Action? KeyNextRequested;

    public event Action<Transform>? EffectTransformCommitted;
    public event Action<int>? EffectStartChanged;
    public event Action<int>? EffectDurationChanged;
    public event Action<int>? EffectLayerChanged;
    public event Action<string>? EffectRenamed;
    public event Action? EffectRelinkRequested;
    public event Action? EffectRemoveRequested;

    public event Action<string>? AudioRenamed;
    public event Action<double>? AudioStartChanged;
    public event Action<double>? AudioTrimChanged;
    public event Action<double>? AudioVolumeChanged;
    public event Action<bool>? AudioMutedChanged;
    public event Action? AudioRelinkRequested;
    public event Action? AudioRemoveRequested;

    private bool _syncing;

    public SequenceInspectorView()
    {
        InitializeComponent();
        foreach (TransformProperty p in Enum.GetValues<TransformProperty>())
            KeyProperty.Items.Add(new ComboBoxItem { Content = p.ToString(), Tag = p });
        KeyProperty.SelectedIndex = 0;

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
        ClipTransform.Committed += t => { if (!_syncing) ClipTransformCommitted?.Invoke(t); };
        FxTransform.Committed += t => { if (!_syncing) EffectTransformCommitted?.Invoke(t); };

        AddKeyButton.Click += (_, _) =>
        {
            if (KeyProperty.SelectedItem is ComboBoxItem item && item.Tag is TransformProperty p)
                KeyAddRequested?.Invoke(p);
        };
        PrevKeyButton.Click += (_, _) => KeyPrevRequested?.Invoke();
        NextKeyButton.Click += (_, _) => KeyNextRequested?.Invoke();
        KeyList.SelectionChanged += (_, _) =>
        {
            if (_syncing || KeyList.SelectedItem is not KeyRow row)
                return;
            KeySelected?.Invoke(row.Property, row.Frame);
        };

        FxName.LostFocus += (_, _) => { if (!_syncing && !string.IsNullOrWhiteSpace(FxName.Text)) EffectRenamed?.Invoke(FxName.Text.Trim()); };
        FxStart.ValueChanged += (_, _) => { if (!_syncing && FxStart.Value is { } v) EffectStartChanged?.Invoke((int)v); };
        FxDuration.ValueChanged += (_, _) => { if (!_syncing && FxDuration.Value is { } v && v > 0) EffectDurationChanged?.Invoke((int)v); };
        FxLayer.ValueChanged += (_, _) => { if (!_syncing && FxLayer.Value is { } v) EffectLayerChanged?.Invoke((int)v); };
        FxRelinkButton.Click += (_, _) => EffectRelinkRequested?.Invoke();
        FxRemoveButton.Click += (_, _) => EffectRemoveRequested?.Invoke();

        AuName.LostFocus += (_, _) => { if (!_syncing && !string.IsNullOrWhiteSpace(AuName.Text)) AudioRenamed?.Invoke(AuName.Text.Trim()); };
        AuStart.ValueChanged += (_, _) => { if (!_syncing && AuStart.Value is { } v) AudioStartChanged?.Invoke((double)v); };
        AuTrim.ValueChanged += (_, _) => { if (!_syncing && AuTrim.Value is { } v) AudioTrimChanged?.Invoke((double)v); };
        AuVolume.ValueChanged += (_, _) => { if (!_syncing && AuVolume.Value is { } v) AudioVolumeChanged?.Invoke((double)v); };
        AuMute.IsCheckedChanged += (_, _) => { if (!_syncing) AudioMutedChanged?.Invoke(AuMute.IsChecked == true); };
        AuRelinkButton.Click += (_, _) => AudioRelinkRequested?.Invoke();
        AuRemoveButton.Click += (_, _) => AudioRemoveRequested?.Invoke();

        ClearAll();
    }

    public void ClearAll()
    {
        _syncing = true;
        try
        {
            ClipAnim.Text = "Select a clip on the timeline";
            ClipFrames.Text = "—";
            KeyList.ItemsSource = null;
            FxImage.Text = "—";
            AuFile.Text = "—";
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowNothing()
    {
        ClipSection.IsVisible = false;
        EffectSection.IsVisible = false;
        AudioSection.IsVisible = false;
    }

    public void ShowClip(string animationId, double speed, int outputFrames, double fps,
        Transform baseTransform, IReadOnlyList<TransformKeyframe> keys)
    {
        ClipSection.IsVisible = true;
        EffectSection.IsVisible = false;
        AudioSection.IsVisible = false;
        _syncing = true;
        try
        {
            ClipAnim.Text = animationId;
            ClipSpeed.Value = (decimal)speed;
            SyncPreset(speed);
            ClipFrames.Text = $"{outputFrames} frames · {outputFrames / fps:F3} s";
            ClipTransform.SetTransform(baseTransform);
            KeyList.ItemsSource = keys
                .OrderBy(k => k.Property).ThenBy(k => k.Frame)
                .Select(k => new KeyRow(k.Property, k.Frame, k.Value, k.Interpolation))
                .ToList();
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowEffect(string name, string imagePath, int start, int duration, int layer,
        Transform baseTransform)
    {
        ClipSection.IsVisible = false;
        EffectSection.IsVisible = true;
        AudioSection.IsVisible = false;
        _syncing = true;
        try
        {
            if (FxName.Text != name)
                FxName.Text = name;
            FxImage.Text = imagePath;
            FxStart.Value = start;
            FxDuration.Value = Math.Max(1, duration);
            FxLayer.Value = layer;
            FxTransform.SetTransform(baseTransform);
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowAudio(string name, string file, double start, double trim, double? duration,
        double volume, bool muted)
    {
        ClipSection.IsVisible = false;
        EffectSection.IsVisible = false;
        AudioSection.IsVisible = true;
        _syncing = true;
        try
        {
            if (AuName.Text != name)
                AuName.Text = name;
            AuFile.Text = file;
            AuStart.Value = (decimal)start;
            AuTrim.Value = (decimal)trim;
            AuVolume.Value = volume;
            AuMute.IsChecked = muted;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ShowKeys(IReadOnlyList<TransformKeyframe> keys)
    {
        _syncing = true;
        try
        {
            KeyList.ItemsSource = keys
                .OrderBy(k => k.Property).ThenBy(k => k.Frame)
                .Select(k => new KeyRow(k.Property, k.Frame, k.Value, k.Interpolation))
                .ToList();
        }
        finally
        {
            _syncing = false;
        }
    }

    public void HighlightKey(TransformProperty? property, int frame)
    {
        if (KeyList.ItemsSource is not IEnumerable<KeyRow> rows)
            return;
        KeyList.SelectedItem = rows.FirstOrDefault(r => r.Property == property && r.Frame == frame);
        if (KeyList.SelectedItem is not null)
            KeyList.ScrollIntoView(KeyList.SelectedItem);
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

    private void OnDeleteKeyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: KeyRow row })
            KeyDeleteRequested?.Invoke(row.Property, row.Frame);
    }
}
