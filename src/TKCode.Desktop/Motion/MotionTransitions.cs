using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Transformation;

namespace TKCode.Desktop.Motion;

/// <summary>
/// 跟随动效开关的属性过渡（控件主题里的悬停 / 按下 / 选中换色等微交互）：
/// 动效关闭时进度恒为 1，等同于立即切换。默认时长 MotionTokens.Micro、强调缓动。
/// </summary>
public abstract class MotionTransition<T> : InterpolatingTransitionBase<T>
{
    protected MotionTransition()
    {
        Duration = MotionTokens.Micro;
        Easing = MotionTokens.EaseEmphasized;
    }

    protected sealed override T Interpolate(double progress, T from, T to) =>
        MotionSettings.IsEnabled ? Lerp(from, to, progress) : to;

    protected abstract T Lerp(T from, T to, double p);
}

/// <summary>纯色画刷插值（其它画刷直接切换）。</summary>
public sealed class MotionBrushTransition : MotionTransition<IBrush?>
{
    protected override IBrush? Lerp(IBrush? from, IBrush? to, double p)
    {
        if (p >= 1 || from is not (ISolidColorBrush or null) || to is not (ISolidColorBrush or null) || (from is null && to is null))
        {
            return to;
        }
        return new ImmutableSolidColorBrush(Mix(Effective(from), Effective(to), p));
    }

    private static Color Effective(IBrush? brush) => brush is ISolidColorBrush s
        ? Color.FromArgb((byte)Math.Round(s.Color.A * Math.Clamp(s.Opacity, 0, 1)), s.Color.R, s.Color.G, s.Color.B)
        : Colors.Transparent;

    /// <summary>
    /// 预乘 alpha 插值：透明端不贡献颜色（Transparent 是“透明的白色”，直接按 ARGB 插值会在中途出现一层半透明的浅灰 / 深灰），
    /// 中间色始终落在两端之间。
    /// </summary>
    public static Color Mix(Color from, Color to, double p)
    {
        var a1 = from.A / 255.0;
        var a2 = to.A / 255.0;
        var a = a1 + (a2 - a1) * p;
        if (a <= 0)
        {
            return Color.FromArgb(0, to.R, to.G, to.B);
        }
        byte Channel(byte c1, byte c2) =>
            (byte)Math.Clamp(Math.Round((c1 * a1 * (1 - p) + c2 * a2 * p) / a), 0, 255);
        return Color.FromArgb((byte)Math.Round(a * 255), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }
}

public sealed class MotionDoubleTransition : MotionTransition<double>
{
    protected override double Lerp(double from, double to, double p) => from + (to - from) * p;
}

public sealed class MotionCornerRadiusTransition : MotionTransition<CornerRadius>
{
    public MotionCornerRadiusTransition()
    {
        Duration = MotionTokens.Medium;
    }

    protected override CornerRadius Lerp(CornerRadius from, CornerRadius to, double p)
    {
        static double L(double a, double b, double t) => a + (b - a) * t;
        return new CornerRadius(L(from.TopLeft, to.TopLeft, p), L(from.TopRight, to.TopRight, p),
            L(from.BottomRight, to.BottomRight, p), L(from.BottomLeft, to.BottomLeft, p));
    }
}

/// <summary>渲染变换插值（按下缩放、悬停上浮）。</summary>
public sealed class MotionTransformTransition : MotionTransition<ITransform?>
{
    protected override ITransform? Lerp(ITransform? from, ITransform? to, double p)
    {
        if (p >= 1)
        {
            return to;
        }
        var a = from as TransformOperations ?? TransformOperations.Identity;
        var b = to as TransformOperations ?? TransformOperations.Identity;
        return TransformOperations.Interpolate(a, b, p);
    }
}
