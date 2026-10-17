using AtomUI.MotionScene;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;

namespace TCode.Desktop.Motion;

/// <summary>
/// TCode 的弹层：AtomUI Popup（关闭会等动画播完）+ TCode 弹层动效（从触发按钮一侧缩放、下落、淡入，带轻微过冲）。
/// 外观（背景、描边、阴影）由内容里的 Border.menu 负责，AtomUI 自带的弹层阴影关掉。
/// </summary>
public class Popover : AtomUI.Desktop.Controls.Popup
{
    protected override Type StyleKeyOverride => typeof(AtomUI.Desktop.Controls.Popup);

    public Popover()
    {
        OpenMotion = new PopoverInMotion();
        CloseMotion = new PopoverOutMotion();
        MotionDuration = MotionTokens.Short;
        IsMotionEnabled = MotionSettings.IsEnabled;
        PopupRootShadow = default;
        OverlayHostShadow = default;
        AttachedToLogicalTree += (_, _) => MotionSettings.Changed += OnMotionSettingsChanged;
        DetachedFromLogicalTree += (_, _) => MotionSettings.Changed -= OnMotionSettingsChanged;
    }

    private void OnMotionSettingsChanged() => IsMotionEnabled = MotionSettings.IsEnabled;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlacementProperty || change.Property == IsVerticalFlippedProperty)
        {
            UpdateOrigin();
        }
    }

    /// <summary>缩放原点贴着触发按钮：向下展开时在上沿，向上（翻转或 Top 放置）时在下沿；右对齐时在右侧。</summary>
    private void UpdateOrigin()
    {
        var name = Placement.ToString();
        var x = name.Contains("Right", StringComparison.Ordinal) ? 0.85 : name.Contains("Left", StringComparison.Ordinal) ? 0.15 : 0.5;
        var up = name.StartsWith("Top", StringComparison.Ordinal) ^ IsVerticalFlipped;
        var origin = new RelativePoint(x, up ? 1 : 0, RelativeUnit.Relative);
        ((PopoverInMotion)OpenMotion!).Origin = origin;
        ((PopoverOutMotion)CloseMotion!).Origin = origin;
        ((PopoverInMotion)OpenMotion!).Drop = up ? 6 : -6;
    }
}

/// <summary>弹层打开：0.94 → 1 缩放、-6px → 0 下落、淡入；原点在左上（弹层向下展开，贴着触发按钮）。</summary>
public sealed class PopoverInMotion : AbstractMotion
{
    public RelativePoint Origin { get; set; } = new(0.15, 0, RelativeUnit.Relative);

    /// <summary>打开起点的竖直位移（向下展开时为负：从按钮方向落下）。</summary>
    public double Drop { get; set; } = -6;

    public PopoverInMotion()
        : base(MotionTokens.Short, MotionTokens.EaseSpring, FillMode.Forward)
    {
    }

    protected override void ConfigureTransitions()
    {
        base.ConfigureTransitions();
        RenderTransformOrigin = Origin;
        // 只有缩放 / 位移用弹簧（允许过冲）。透明度绝不能超过 1：渲染时按字节换算，1.04 × 255 溢出成接近 0，
        // 弹层会在快结束时几乎消失几帧（“打开下拉闪几下”）
        foreach (var transition in Transitions.OfType<TransitionBase>())
        {
            if (transition.Property == Visual.OpacityProperty)
            {
                transition.Easing = MotionTokens.EaseEmphasized;
            }
        }
    }

    protected override void ConfigureMotionStartValue(BaseMotionActor actor)
    {
        actor.Opacity = 0;
        actor.MotionTransform = BuildTranslateScaleAndTransform(0.94, 0.94, 0, Drop);
    }

    protected override void ConfigureMotionEndValue(BaseMotionActor actor)
    {
        actor.Opacity = 1;
        actor.MotionTransform = BuildTranslateScaleAndTransform(1, 1, 0, 0);
    }
}

/// <summary>弹层关闭：快速收回并淡出，不过冲。</summary>
public sealed class PopoverOutMotion : AbstractMotion
{
    public RelativePoint Origin { get; set; } = new(0.15, 0, RelativeUnit.Relative);

    public PopoverOutMotion()
        : base(MotionTokens.Short, new CubicEaseIn(), FillMode.Forward)
    {
    }

    protected override void ConfigureTransitions()
    {
        base.ConfigureTransitions();
        RenderTransformOrigin = Origin;
    }

    protected override void ConfigureMotionStartValue(BaseMotionActor actor)
    {
        actor.Opacity = 1;
        actor.MotionTransform = BuildTranslateScaleAndTransform(1, 1, 0, 0);
    }

    protected override void ConfigureMotionEndValue(BaseMotionActor actor)
    {
        actor.Opacity = 0;
        actor.MotionTransform = BuildTranslateScaleAndTransform(0.96, 0.96, 0, -4);
    }

    protected override void NotifyCompleted(BaseMotionActor actor)
    {
        actor.Opacity = 1;
        actor.MotionTransform = null;
    }
}
