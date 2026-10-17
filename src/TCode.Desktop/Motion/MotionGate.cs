using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TCode.Desktop.Motion;

/// <summary>
/// 判断一次状态变化是否该播动画：
/// - 控件已加载（首帧之前的初始值、绑定求值都直接跳到终态）；
/// - 所在区域可见；
/// - 不是刚换了 DataContext（虚拟化列表回收容器、切换会话时，状态变化来自新数据而不是用户操作）。
///   DataContext 变化时打上“本轮不播”的标记，这一轮的绑定更新都跳到终态，布局完成后（Background）清除。
/// </summary>
internal sealed class MotionGate
{
    private static readonly AttachedProperty<MotionGate?> GateProperty =
        AvaloniaProperty.RegisterAttached<Control, MotionGate?>("Gate", typeof(MotionGate));

    private readonly Control _control;
    private bool _suppressed;
    private bool _clearScheduled;

    static MotionGate()
    {
        // 类处理器先于绑定（实例订阅）收到 DataContext 变化，所以标记总在绑定更新之前
        StyledElement.DataContextProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.GetValue(GateProperty) is { } gate)
            {
                gate.Suppress();
            }
            else if (control is IMotionGated || control.IsSet(Presence.IsPresentProperty) || control.IsSet(Turn.IsOnProperty))
            {
                For(control).Suppress();
            }
        });
    }

    private MotionGate(Control control)
    {
        _control = control;
    }

    public static MotionGate For(Control control)
    {
        if (control.GetValue(GateProperty) is not { } gate)
        {
            gate = new MotionGate(control);
            control.SetValue(GateProperty, gate);
        }
        return gate;
    }

    private void Suppress()
    {
        _suppressed = true;
        if (_clearScheduled)
        {
            return;
        }
        _clearScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _clearScheduled = false;
            _suppressed = false;
        }, DispatcherPriority.Background);
    }

    public bool ShouldAnimate() =>
        !_suppressed && _control.IsLoaded && _control.GetVisualParent() is Visual { IsEffectivelyVisible: true } && MotionSettings.IsEnabled;
}

/// <summary>自带动效状态的控件（Reveal、SmoothSize）：DataContext 变化时同样不播。</summary>
internal interface IMotionGated;
