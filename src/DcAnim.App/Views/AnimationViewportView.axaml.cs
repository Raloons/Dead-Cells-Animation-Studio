using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DcAnim.App.Controls;

namespace DcAnim.App.Views;

/// <summary>Center viewport: title, checkerboard canvas, fit/zoom controls.</summary>
public sealed partial class AnimationViewportView : UserControl
{
    public event Action? FitRequested;

    private bool _userZoomed;

    public AnimationViewportView()
    {
        InitializeComponent();
        ZoomSlider.ValueChanged += (_, _) =>
        {
            Preview.Zoom = ZoomSlider.Value;
            ZoomLabel.Text = $"{(int)Math.Round(ZoomSlider.Value * 100)}%";
        };
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.Property == PreviewControl.ZoomProperty)
                _userZoomed = true;
        };
        CheckerButton.IsCheckedChanged += (_, _) =>
            Preview.ShowChecker = CheckerButton.IsChecked ?? true;
        FitButton.Click += (_, _) => FitRequested?.Invoke();
        Preview.Zoom = ZoomSlider.Value;
        Preview.ShowChecker = true;
        SizeChanged += (_, _) => AutoFitOnFirstLayout();
    }

    private void AutoFitOnFirstLayout()
    {
        if (_userZoomed || _autoFitFrame <= 0 || Preview.Bounds.Width <= 0)
            return;
        FitToView(_autoFitFrame, _autoFitFrame);
    }

    private int _autoFitFrame = -1;

    /// <summary>Notes the frame size for layout-driven auto-fit (until manual zoom).</summary>
    public void NoteFrameSize(int frameWidthHeight) => _autoFitFrame = frameWidthHeight;

    public void ResetUserZoom() => _userZoomed = false;

    public void SetFrame(Bitmap? frame) => Preview.Frame = frame;

    public void SetTitle(string title, string subtitle)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }

    public void SetZoom(double zoom)
    {
        ZoomSlider.Value = Math.Clamp(zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    public double CurrentZoom => Preview.Zoom;

    /// <summary>Zoom so the frame fits the current viewport size.</summary>
    public void FitToView(int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || Preview.Bounds.Width <= 0 || Preview.Bounds.Height <= 0)
            return;
        double fit = Math.Min(Preview.Bounds.Width / frameWidth, Preview.Bounds.Height / frameHeight);
        fit = Math.Floor(fit);
        if (fit >= 1)
            SetZoom(Math.Min(fit, ZoomSlider.Maximum));
        _userZoomed = false; // programmatic fit never counts as a manual zoom
    }
}
