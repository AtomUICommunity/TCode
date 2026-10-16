using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace TCode.Desktop.Motion;

/// <summary>
/// 判断一次状态变化是否该播动画：控件已加载（首帧之前的初始值、绑定求值都直接跳到终态），
/// 且 DataContext 没有换（虚拟化列表回收容器时换了数据，状态变化不是用户操作，不播）。
/// </summary>
internal sealed class MotionGate
{
    private readonly Control _control;
    private object? _dataContext;

    public MotionGate(Control control)
    {
        _control = control;
        _dataContext = control.DataContext;
    }

    public bool ShouldAnimate()
    {
        var dataContext = _control.DataContext;
        var same = ReferenceEquals(dataContext, _dataContext);
        _dataContext = dataContext;
        return same && _control.IsLoaded && _control.GetVisualParent() is Visual { IsEffectivelyVisible: true } && MotionSettings.IsEnabled;
    }
}
