using CommunityToolkit.Mvvm.ComponentModel;

namespace PadShift.App.ViewModels;

/// <summary>
/// One clickable control of the drawn gamepad. Geometry is in a fixed 420x300 coordinate space;
/// the view scales the whole pad with a <c>Viewbox</c>, so no layout math is needed here.
/// </summary>
public sealed class GamepadControlViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isPressed;

    public GamepadControlViewModel(
        long buttonId,
        string label,
        string caption,
        double x,
        double y,
        double width,
        double height,
        double radius)
    {
        ButtonId = buttonId;
        Label = label;
        Caption = caption;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Radius = radius;
    }

    /// <summary>the reference tool <c>GamepadButton</c> id written into <c>masks[].set[].buttonId</c>.</summary>
    public long ButtonId { get; }

    /// <summary>Short text drawn on the pad (A, LB, LT, P1, ...).</summary>
    public string Label { get; }

    /// <summary>Longer Chinese description used in tooltips and the binding panel.</summary>
    public string Caption { get; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Corner radius for the drawn shape; half the shorter side makes a circle.</summary>
    public double Radius { get; }

    /// <summary>True for round controls (sticks, face buttons); the view picks the matching template.</summary>
    public bool IsRound => Radius >= (Math.Min(Width, Height) / 2) - 0.5;

    public string Tooltip => $"{Caption}（按钮 {ButtonId}）";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// True while the physical control is held. The view uses it to light the hit area up; the
    /// setter only raises <c>PropertyChanged</c> on an actual change, so a 20 Hz poll is cheap.
    /// </summary>
    public bool IsPressed
    {
        get => _isPressed;
        set => SetProperty(ref _isPressed, value);
    }
}
