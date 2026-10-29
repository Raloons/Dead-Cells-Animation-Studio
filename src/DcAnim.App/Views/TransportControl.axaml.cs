using Avalonia;
using Avalonia.Controls;

namespace DcAnim.App.Views;

/// <summary>
/// Compact transport bar with a unified monochrome vector icon set.
/// Emits intent events; the host owns the <c>PlaybackEngine</c>.
/// </summary>
public sealed partial class TransportControl : UserControl
{
    public event Action? PlayPauseRequested;
    public event Action? FirstRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? LastRequested;
    public event Action<double>? SpeedChanged;
    public event Action<double>? FpsChanged;
    public event Action<bool>? LoopChanged;

    public TransportControl()
    {
        InitializeComponent();
        PlayButton.Click += (_, _) => PlayPauseRequested?.Invoke();
        FirstButton.Click += (_, _) => FirstRequested?.Invoke();
        PrevButton.Click += (_, _) => PreviousRequested?.Invoke();
        NextButton.Click += (_, _) => NextRequested?.Invoke();
        LastButton.Click += (_, _) => LastRequested?.Invoke();
        LoopCheck.IsCheckedChanged += (_, _) => LoopChanged?.Invoke(LoopCheck.IsChecked ?? true);
        SpeedBox.SelectionChanged += (_, _) =>
        {
            if (SpeedBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), out double s))
                SpeedChanged?.Invoke(s);
        };
        FpsBox.SelectionChanged += (_, _) =>
        {
            if (FpsBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), out double fps) && fps > 0)
                FpsChanged?.Invoke(fps);
        };
    }

    public void SetPlaying(bool playing)
    {
        if (Application.Current?.TryGetResource(playing ? "IconPause" : "IconPlay", null, out var icon) == true
            && icon is Avalonia.Media.Geometry geometry)
        {
            PlayIcon.Data = geometry;
        }
    }

    public void SetEnabled(bool enabled)
    {
        FirstButton.IsEnabled = enabled;
        PrevButton.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
        LastButton.IsEnabled = enabled;
        SpeedBox.IsEnabled = enabled;
        FpsBox.IsEnabled = enabled;
    }
}
