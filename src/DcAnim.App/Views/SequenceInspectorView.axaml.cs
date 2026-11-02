using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using DcAnim.Core.Sequence;
using System.ComponentModel;

namespace DcAnim.App.Views;

public sealed record KeyRow(TransformProperty Property, int Frame, double Value, KeyframeInterpolation Interpolation)
{
    public string Label => $"{Property} @ {Frame} = {Value:0.##} ({Interpolation})";
}

/// <summary>Donor gallery row: weapon animation ID plus async thumbnail.</summary>
public sealed class DonorGalleryItem : INotifyPropertyChanged
{
    private WriteableBitmap? _thumbnail;
    public required string Id { get; init; }
    public required int FrameCount { get; init; }
    public string Meta => $"{FrameCount} frames";
    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
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
    public event Action<int>? ClipTrimInChanged; // 1-based source frame
    public event Action<int>? ClipTrimOutChanged; // 1-based source frame, inclusive
    public event Action<bool>? ClipReversedChanged;
    public event Action<int>? ClipHoldChanged; // tail hold output frames
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

    public event Action<bool>? OnionEnabledChanged;
    public event Action<int>? OnionCountChanged;
    public event Action<double>? OnionOpacityChanged;

    public event Action<bool>? WeaponEnabledChanged;
    public event Action? WeaponPickDonorRequested;
    public event Action? WeaponAutoRequested;
    public event Action? WeaponClearRequested;
    public event Action? WeaponImportRequested;
    public event Action? WeaponExportRequested;
    public event Action<int>? WeaponFrameStepRequested; // -1 prev, +1 next
    public event Action<WeaponPose>? WeaponPoseApplied;
    public event Action? WeaponFlipRequested;
    public event Action? WeaponFlipAllRequested;
    public event Action<bool>? WeaponMoveChanged;
    public event Action<bool>? WeaponBehindChanged;
    public event Action<bool>? WeaponPreserveChanged;
    public event Action<bool>? WeaponNonBodyChanged;
    public event Action<int>? WeaponBodyColorChanged;
    public event Action<string?>? WeaponDonorHovered;
    public event Action<string>? WeaponDonorPicked;

    private readonly List<DonorGalleryItem> _donorAll = new();
    private string? _donorHoverId;

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

        ClipTrimIn.ValueChanged += (_, _) => { if (!_syncing && ClipTrimIn.Value is { } v) ClipTrimInChanged?.Invoke(Math.Max(1, (int)v)); };
        ClipTrimOut.ValueChanged += (_, _) => { if (!_syncing && ClipTrimOut.Value is { } v) ClipTrimOutChanged?.Invoke(Math.Max(1, (int)v)); };
        ClipReverse.IsCheckedChanged += (_, _) => { if (!_syncing) ClipReversedChanged?.Invoke(ClipReverse.IsChecked == true); };
        ClipHold.ValueChanged += (_, _) => { if (!_syncing && ClipHold.Value is { } v) ClipHoldChanged?.Invoke(Math.Max(0, (int)v)); };

        OnionEnabled.IsCheckedChanged += (_, _) => { if (!_syncing) OnionEnabledChanged?.Invoke(OnionEnabled.IsChecked == true); };
        OnionCount.ValueChanged += (_, _) => { if (!_syncing && OnionCount.Value is { } v) OnionCountChanged?.Invoke(Math.Clamp((int)v, 1, 3)); };
        OnionOpacity.ValueChanged += (_, _) => { if (!_syncing) OnionOpacityChanged?.Invoke(OnionOpacity.Value); };

        WeaponEnabled.IsCheckedChanged += (_, _) => { if (!_syncing) WeaponEnabledChanged?.Invoke(WeaponEnabled.IsChecked == true); };
        WeaponPickButton.Click += (_, _) => WeaponPickDonorRequested?.Invoke();
        WeaponAutoButton.Click += (_, _) => WeaponAutoRequested?.Invoke();
        WeaponClearButton.Click += (_, _) => WeaponClearRequested?.Invoke();
        WeaponImportButton.Click += (_, _) => WeaponImportRequested?.Invoke();
        WeaponExportButton.Click += (_, _) => WeaponExportRequested?.Invoke();
        WeaponPrevButton.Click += (_, _) => WeaponFrameStepRequested?.Invoke(-1);
        WeaponNextButton.Click += (_, _) => WeaponFrameStepRequested?.Invoke(1);
        WeaponFlipButton.Click += (_, _) => WeaponFlipRequested?.Invoke();
        WeaponFlipAllButton.Click += (_, _) => WeaponFlipAllRequested?.Invoke();
        foreach (var n in new NumericUpDown[] { WeaponX, WeaponY, WeaponRot, WeaponSx, WeaponSy })
        {
            n.ValueChanged += (_, _) =>
            {
                if (_syncing || n.Value is null)
                    return;
                WeaponPoseApplied?.Invoke(ReadWeaponPose());
            };
        }
        WeaponBehind.IsCheckedChanged += (_, _) => { if (!_syncing) WeaponBehindChanged?.Invoke(WeaponBehind.IsChecked == true); };
        WeaponPreserve.IsCheckedChanged += (_, _) => { if (!_syncing) WeaponPreserveChanged?.Invoke(WeaponPreserve.IsChecked == true); };
        WeaponNonBody.IsCheckedChanged += (_, _) => { if (!_syncing) WeaponNonBodyChanged?.Invoke(WeaponNonBody.IsChecked == true); };
        WeaponBodyColor.ValueChanged += (_, _) => { if (!_syncing && WeaponBodyColor.Value is { } v) WeaponBodyColorChanged?.Invoke((int)v); };
        WeaponMoveCheck.IsCheckedChanged += (_, _) => { if (!_syncing) WeaponMoveChanged?.Invoke(WeaponMoveCheck.IsChecked == true); };
        WeaponGallerySearch.TextChanged += (_, _) => ApplyDonorFilter();
        WeaponGallery.SelectionChanged += (_, _) =>
        {
            if (_syncing || WeaponGallery.SelectedItem is not DonorGalleryItem item)
                return;
            WeaponDonorPicked?.Invoke(item.Id);
        };
        WeaponGallery.AddHandler(ListBoxItem.PointerEnteredEvent, (_, e) =>
        {
            if (e.Source is Avalonia.StyledElement se && se.DataContext is DonorGalleryItem item)
                SetDonorHover(item.Id);
        }, RoutingStrategies.Tunnel);
        WeaponGallery.AddHandler(ListBoxItem.PointerExitedEvent, (_, _) => SetDonorHover(null), RoutingStrategies.Tunnel);
        WeaponGallery.AddHandler(InputElement.PointerExitedEvent, (_, _) => SetDonorHover(null), RoutingStrategies.Tunnel);

        ClearAll();
    }

    private void SetDonorHover(string? id)
    {
        if (string.Equals(_donorHoverId, id, StringComparison.Ordinal))
            return;
        _donorHoverId = id;
        WeaponDonorHovered?.Invoke(id);
    }

    public void SetDonorGallery(IReadOnlyList<(string Id, int Frames)> items)
    {
        _syncing = true;
        try
        {
            _donorAll.Clear();
            foreach (var (id, frames) in items)
                _donorAll.Add(new DonorGalleryItem { Id = id, FrameCount = frames });
            ApplyDonorFilter();
        }
        finally
        {
            _syncing = false;
        }
    }

    public void ForEachDonorItem(Action<DonorGalleryItem> action)
    {
        foreach (var item in _donorAll)
            action(item);
    }

    private void ApplyDonorFilter()
    {
        string q = WeaponGallerySearch.Text?.Trim() ?? string.Empty;
        WeaponGallery.ItemsSource = string.IsNullOrEmpty(q)
            ? _donorAll.ToList()
            : _donorAll.Where(i => i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public string WeaponGalleryFilter => WeaponGallerySearch.Text?.Trim() ?? string.Empty;

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
        WeaponSection.IsVisible = false;
        EffectSection.IsVisible = false;
        AudioSection.IsVisible = false;
    }

    public void ShowClip(string animationId, double speed, int outputFrames, double fps,
        Transform baseTransform, IReadOnlyList<TransformKeyframe> keys,
        int sourceFirstOneBased, int sourceLastOneBased, int sourceCount,
        bool reversed, int tailHold)
    {
        ClipSection.IsVisible = true;
        WeaponSection.IsVisible = true;
        EffectSection.IsVisible = false;
        AudioSection.IsVisible = false;
        _syncing = true;
        try
        {
            ClipAnim.Text = animationId;
            ClipSpeed.Value = (decimal)speed;
            SyncPreset(speed);
            ClipFrames.Text = $"{outputFrames} frames · {outputFrames / fps:F3} s";
            ClipTrimIn.Value = Math.Max(1, sourceFirstOneBased);
            ClipTrimOut.Value = Math.Max(1, sourceLastOneBased);
            ClipTrimIn.Maximum = Math.Max(1, sourceCount);
            ClipTrimOut.Maximum = Math.Max(1, sourceCount);
            bool knownSources = sourceCount > 0;
            ClipTrimIn.IsEnabled = knownSources;
            ClipTrimOut.IsEnabled = knownSources;
            ClipReverse.IsChecked = reversed;
            ClipHold.Value = Math.Max(0, tailHold);
            ClipMapping.Text = knownSources
                ? $"Src {sourceFirstOneBased}–{sourceLastOneBased} of {sourceCount}" +
                  (reversed ? " · reversed" : "") +
                  (tailHold > 0 ? $" · +{tailHold} hold" : "")
                : "Source frames unknown (missing animation)";
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
        WeaponSection.IsVisible = false;
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
        WeaponSection.IsVisible = false;
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

    public void ShowProject(string name, double fps, int clipCount, int totalFrames,
        bool onionEnabled, int onionCount, double onionOpacity)
    {
        _syncing = true;
        try
        {
            if (ProjectName.Text != name)
                ProjectName.Text = name;
            ProjectFps.Value = (decimal)fps;
            SeqClips.Text = clipCount.ToString();
            SeqTotal.Text = $"{totalFrames} frames · {totalFrames / fps:F3} s";
            OnionEnabled.IsChecked = onionEnabled;
            OnionCount.Value = Math.Clamp(onionCount, 1, 3);
            OnionOpacity.Value = onionOpacity;
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

    public void ShowWeapon(WeaponClipOverride? weapon, int currentSourceFrame, int frameCount)
    {
        _syncing = true;
        try
        {
            bool has = weapon is { Enabled: true };
            WeaponEnabled.IsChecked = has;
            WeaponDonor.Text = weapon?.DonorAnimationId ?? "AtkKatanaB2";
            WeaponDonorFrame.Value = weapon?.DonorFrame ?? 0;
            WeaponBodyGreen.Value = weapon?.BodyMaskGreen ?? 32;
            WeaponDonorGreen.Value = weapon?.DonorMaskGreen ?? 159;
            WeaponNonBody.IsChecked = weapon?.NonBodyMode ?? false;
            WeaponBodyColor.Value = weapon?.BodyColorGreen ?? 96;
            WeaponBehind.IsChecked = weapon?.BehindBody ?? false;
            WeaponPreserve.IsChecked = weapon?.PreserveBody ?? true;
            if (weapon is null || weapon.Frames.Count == 0)
            {
                WeaponStatus.Text = weapon is null ? "No weapon override on this clip." : "Override has no frames — run Auto-detect.";
                WeaponFrameLabel.Text = "—";
            }
            else
            {
                var f = weapon.ForSource(currentSourceFrame) ?? weapon.Frames[0];
                WeaponStatus.Text = $"{weapon.Frames.Count} posed frames · donor {weapon.DonorAnimationId}[{weapon.DonorFrame}] " +
                    $"{weapon.DonorRegion.Count} runs · pivot {weapon.DonorPivotX:0.#},{weapon.DonorPivotY:0.#}";
                WeaponFrameLabel.Text = $"Src {f.SourceFrame} · {f.Remove.Count} remove runs · {f.Pose.Degrees:0.#}° @ {f.Pose.X:0.#},{f.Pose.Y:0.#}";
                WeaponX.Value = (decimal)f.Pose.X;
                WeaponY.Value = (decimal)f.Pose.Y;
                WeaponRot.Value = (decimal)f.Pose.Degrees;
                WeaponSx.Value = (decimal)Math.Clamp(f.Pose.ScaleX, 0.1, 8);
                WeaponSy.Value = (decimal)Math.Clamp(f.Pose.ScaleY, 0.1, 8);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    public string WeaponDonorId => WeaponDonor.Text?.Trim() ?? string.Empty;
    public int WeaponDonorFrameValue => (int)(WeaponDonorFrame.Value ?? 0);
    public int WeaponBodyGreenValue => (int)(WeaponBodyGreen.Value ?? 32);
    public int WeaponDonorGreenValue => (int)(WeaponDonorGreen.Value ?? 159);
    public bool WeaponNonBodyValue => WeaponNonBody.IsChecked == true;
    public int WeaponBodyColorValue => (int)(WeaponBodyColor.Value ?? 96);

    public void SetDonor(string animationId)
    {
        _syncing = true;
        try { WeaponDonor.Text = animationId; }
        finally { _syncing = false; }
    }

    private WeaponPose ReadWeaponPose() => new(
        Math.Clamp(WeaponX.Value is { } vx ? (double)vx : 0, -2000, 2000),
        Math.Clamp(WeaponY.Value is { } vy ? (double)vy : 0, -2000, 2000),
        WeaponRot.Value is { } vr ? (double)vr : 0,
        Math.Clamp(WeaponSx.Value is { } vsx ? (double)vsx : 1, 0.1, 8),
        Math.Clamp(WeaponSy.Value is { } vsy ? (double)vsy : 1, 0.1, 8));
}
