using Avalonia.Animation.Easings;

namespace TKCode.Desktop.Motion;

/// <summary>
/// TKCode 动效规范（偏丰富灵动）：所有动效只从这里取曲线与时长，见 docs/design/motion.md。
/// </summary>
public static class MotionTokens
{
    public static readonly TimeSpan Micro = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan Medium = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Long = TimeSpan.FromMilliseconds(360);

    /// <summary>进入、展开：cubic-bezier(0.2, 0, 0, 1)。</summary>
    public static readonly Easing EaseEmphasized = new SplineEasing(0.2, 0, 0, 1);

    /// <summary>退出、收起：cubic-bezier(0.3, 0, 1, 1)。</summary>
    public static readonly Easing EaseExit = new SplineEasing(0.3, 0, 1, 1);

    /// <summary>轻微过冲的弹簧缓动（弹层、浮层缩放）。</summary>
    public static readonly Easing EaseSpring = new SpringEase(0.72);

    /// <summary>展开、面板：稳而不拖，几乎不过冲。</summary>
    public static readonly SpringCurve Standard = new(0.38, 0.86);

    /// <summary>出现、弹出：明显但克制的过冲。</summary>
    public static readonly SpringCurve Bouncy = new(0.32, 0.7);

    /// <summary>收起、消失：比出现快，没有过冲。</summary>
    public static readonly SpringCurve Exit = new(0.26, 1.0);

    /// <summary>位置跟随（FLIP 变形、滑动指示条）。</summary>
    public static readonly SpringCurve Glide = new(0.34, 0.82);

    /// <summary>页面切换（设置页）。</summary>
    public static readonly SpringCurve Gentle = new(0.44, 0.95);

    public static readonly TweenCurve FadeOut = new(TimeSpan.FromMilliseconds(150), EaseExit);

    /// <summary>列表逐项入场的错开间隔与上限。</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(24);
    public const int StaggerLimit = 8;
}
