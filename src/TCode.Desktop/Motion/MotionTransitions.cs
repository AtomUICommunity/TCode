using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Transformation;

namespace TCode.Desktop.Motion;

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
        if (p >= 1)
        {
            return to;
        }
        Color? a = from switch
        {
            ISolidColorBrush s => s.Color,
            null => to is ISolidColorBrush t ? Color.FromArgb(0, t.Color.R, t.Color.G, t.Color.B) : null,
            _ => null,
        };
        Color? b = to switch
        {
            ISolidColorBrush s => s.Color,
            null => from is ISolidColorBrush f ? Color.FromArgb(0, f.Color.R, f.Color.G, f.Color.B) : null,
            _ => null,
        };
        if (a is not { } ca || b is not { } cb)
        {
            return to;
        }
        static byte Lerp(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        var opacity = (from?.Opacity ?? 1) + ((to?.Opacity ?? 1) - (from?.Opacity ?? 1)) * p;
        return new ImmutableSolidColorBrush(
            Color.FromArgb(Lerp(ca.A, cb.A, p), Lerp(ca.R, cb.R, p), Lerp(ca.G, cb.G, p), Lerp(ca.B, cb.B, p)), opacity);
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
