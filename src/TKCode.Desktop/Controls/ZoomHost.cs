using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Config;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 界面缩放的当前比例（settings.json 的 uiScale）。<see cref="ZoomHost"/> 订阅它；需要按比例换算窗口外壳尺寸的代码也订阅它。
/// 与 <c>MotionSettings</c> 一样是进程内的静态状态：弹层内容在独立的可视树里，沿用静态值比沿树继承更直接。
/// </summary>
public static class UiZoom
{
    private static double _scale = UiScaleLevels.Default;

    public static event Action? Changed;

    public static double Scale
    {
        get => _scale;
        set
        {
            value = UiScaleLevels.Clamp(value);
            if (Math.Abs(_scale - value) > 1e-9)
            {
                _scale = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>跟随 settings.json 的 uiScale：立即同步一次，此后设置页、快捷键或外部编辑改了文件都会同步。</summary>
    public static void Follow(UserSettingsService settings)
    {
        Scale = settings.UiScale;
        settings.Changed += () => Scale = settings.UiScale;
    }
}

/// <summary>
/// 把内容按 <see cref="UiZoom.Scale"/> 等比缩放：先按缩放后的可用空间排版，再整体放大，所以文字保持矢量清晰、布局不会错位。
/// 主窗口的内容和每个菜单 / 选择器弹层的内容各包一层——弹层是独立窗口，外层的缩放覆盖不到。
/// </summary>
public sealed class ZoomHost : LayoutTransformControl
{
    protected override Type StyleKeyOverride => typeof(LayoutTransformControl);

    public ZoomHost() => Apply();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UiZoom.Changed += Apply;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UiZoom.Changed -= Apply;
        base.OnDetachedFromVisualTree(e);
    }

    private void Apply()
    {
        var scale = UiZoom.Scale;
        LayoutTransform = UiScaleLevels.IsDefault(scale) ? null : new ScaleTransform(scale, scale);
    }
}
