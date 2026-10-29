using Avalonia.Controls;
using Avalonia.Input;
using DcAnim.Core.Sequence;

namespace DcAnim.App.Views;

/// <summary>
/// Numeric transform editor. Commits once per edit (LostFocus / toggle), so
/// spinner drags and typing don't flood the undo history.
/// </summary>
public sealed partial class TransformEditorView : UserControl
{
    public event Action<Transform>? Committed;

    private bool _syncing;

    public TransformEditorView()
    {
        InitializeComponent();
        foreach (var n in new NumericUpDown[] { NumX, NumY, NumSx, NumSy, NumRot, NumOp, NumPx, NumPy })
        {
            n.LostFocus += (_, _) => CommitIfDirty();
            n.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    CommitIfDirty();
            };
        }
        FlipHCheck.IsCheckedChanged += (_, _) => CommitIfDirty();
        FlipVCheck.IsCheckedChanged += (_, _) => CommitIfDirty();
    }

    public void SetTransform(Transform t)
    {
        _syncing = true;
        try
        {
            NumX.Value = (decimal)t.X;
            NumY.Value = (decimal)t.Y;
            NumSx.Value = (decimal)t.ScaleX;
            NumSy.Value = (decimal)t.ScaleY;
            NumRot.Value = (decimal)t.RotationDegrees;
            NumOp.Value = (decimal)t.Opacity;
            NumPx.Value = (decimal)t.PivotX;
            NumPy.Value = (decimal)t.PivotY;
            FlipHCheck.IsChecked = t.FlipH;
            FlipVCheck.IsChecked = t.FlipV;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CommitIfDirty()
    {
        if (_syncing)
            return;
        Committed?.Invoke(Read());
    }

    private Transform Read() => new()
    {
        X = NumX.Value is { } vx ? (double)vx : 0,
        Y = NumY.Value is { } vy ? (double)vy : 0,
        ScaleX = Math.Max(0.01, NumSx.Value is { } vsx ? (double)vsx : 1),
        ScaleY = Math.Max(0.01, NumSy.Value is { } vsy ? (double)vsy : 1),
        RotationDegrees = NumRot.Value is { } vr ? (double)vr : 0,
        Opacity = Math.Clamp(NumOp.Value is { } vo ? (double)vo : 1, 0, 1),
        PivotX = NumPx.Value is { } vpx ? (double)vpx : 0.5,
        PivotY = NumPy.Value is { } vpy ? (double)vpy : 0.5,
        FlipH = FlipHCheck.IsChecked == true,
        FlipV = FlipVCheck.IsChecked == true,
    };
}
