using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TCode.Desktop.Motion;

/// <summary>
/// 旋转到某个状态（展开箭头 › → ⌄）：motion:Turn.IsOn="{Binding IsExpanded}"，默认转 90°。
/// 替代“两个图标按 IsVisible 互换”。
/// </summary>
public static class Turn
{
    public static readonly AttachedProperty<bool> IsOnProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOn", typeof(Turn));

    public static readonly AttachedProperty<double> AngleProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Angle", typeof(Turn), 90);

    private static readonly AttachedProperty<Controller?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<Control, Controller?>("Controller", typeof(Turn));

    static Turn()
    {
        IsOnProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (control.GetValue(ControllerProperty) is not { } controller)
            {
                controller = new Controller(control);
                control.SetValue(ControllerProperty, controller);
            }
            controller.Update(e.GetNewValue<bool>());
        });
    }

    public static bool GetIsOn(Control control) => control.GetValue(IsOnProperty);
    public static void SetIsOn(Control control, bool value) => control.SetValue(IsOnProperty, value);
    public static double GetAngle(Control control) => control.GetValue(AngleProperty);
    public static void SetAngle(Control control, double value) => control.SetValue(AngleProperty, value);

    private sealed class Controller
    {
        private readonly Control _control;
        private readonly MotionGate _gate;
        private readonly MotionValue _angle;
        private readonly RotateTransform _rotate = new();

        public Controller(Control control)
        {
            _control = control;
            _gate = new MotionGate(control);
            control.RenderTransform = _rotate;
            control.RenderTransformOrigin = RelativePoint.Center;
            _angle = new MotionValue(control, 0, a => _rotate.Angle = a);
        }

        public void Update(bool on)
        {
            var target = on ? GetAngle(_control) : 0;
            if (_gate.ShouldAnimate())
            {
                _angle.AnimateTo(target, MotionTokens.Bouncy);
            }
            else
            {
                _angle.Snap(target);
            }
        }
    }
}
