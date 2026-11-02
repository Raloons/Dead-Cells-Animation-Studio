using Avalonia.Controls;
using DcAnim.Core.Model;

namespace DcAnim.App.Views;

/// <summary>Right properties panel: read-only Animation / Timing / Source sections.</summary>
public sealed partial class AnimationInspectorView : UserControl
{
    public AnimationInspectorView()
    {
        InitializeComponent();
        Clear();
    }

    public void Clear()
    {
        InspId.Text = "-";
        InspFrames.Text = "-";
        InspDuration.Text = "-";
        InspCurrent.Text = "-";
        InspFps.Text = "-";
        InspHit.Text = "-";
        InspCharge.Text = "-";
        InspFx.Text = "-";
        InspTimingSrc.Text = "-";
        InspAtlas.Text = "-";
        InspCanvas.Text = "-";
    }

    public void ShowClip(AnimationClip clip, double fps)
    {
        InspId.Text = clip.Id;
        InspFrames.Text = clip.FrameCount.ToString();
        InspDuration.Text = $"{clip.GetDuration().TotalSeconds:F3} s";
        InspFps.Text = $"{fps:0.#} (assumed uniform)";
        InspHit.Text = clip.Timing?.HitFrame?.ToString() ?? "-";
        InspCharge.Text = clip.Timing?.ChargeSeconds is { } c ? $"{c:F2} s" : "-";
        InspFx.Text = clip.Timing?.FxId ?? "-";
        InspTimingSrc.Text = clip.Timing?.SourceNote ?? "Uniform 1/FPS (assumed; see docs).";
        InspAtlas.Text = clip.SourceDescription;
        var first = clip.Frames.Count > 0 ? clip.Frames[0] : null;
        InspCanvas.Text = first is null ? "-" : $"{first.Width} × {first.Height} px / frame";
        InspCurrent.Text = clip.FrameCount > 0 ? $"1 / {clip.FrameCount}" : "-";
    }

    public void SetPosition(int frameIndex, int frameCount, string regionName)
    {
        InspCurrent.Text = frameCount <= 0 ? "-" : $"{frameIndex + 1} / {frameCount}  ({regionName})";
    }
}
