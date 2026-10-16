using Avalonia.Animation.Easings;

namespace TCode.Desktop.Motion;

/// <summary>
/// 动效曲线：弹簧（物理模拟，反向打断时保留速度，不会跳变）或定时补间（固定时长 + 缓动）。
/// </summary>
public abstract record MotionCurve;

/// <summary>
/// 弹簧：response 为无阻尼时的周期（秒，越小越快），dampingRatio 小于 1 时有过冲（越小越弹）。参数含义与 SwiftUI 的 spring 一致。
/// </summary>
public sealed record SpringCurve(double Response, double DampingRatio) : MotionCurve
{
    public double Stiffness => Math.Pow(2 * Math.PI / Response, 2);

    public double Damping => 4 * Math.PI * DampingRatio / Response;
}

public sealed record TweenCurve(TimeSpan Duration, Easing Easing) : MotionCurve;

/// <summary>
/// 归一化的欠阻尼弹簧缓动（t=1 时已静止在 1），给只接受 Easing 的场合（AtomUI 弹层动效、Avalonia Transitions）使用。
/// </summary>
public sealed class SpringEase : Easing
{
    private readonly double _zeta;
    private readonly double _omega;
    private readonly double _omegaD;

    public SpringEase() : this(0.72)
    {
    }

    public SpringEase(double dampingRatio)
    {
        _zeta = Math.Clamp(dampingRatio, 0.3, 0.99);
        // 包络 e^(-ζωt) 在 t=1 时衰减到 0.1%
        _omega = 6.9 / _zeta;
        _omegaD = _omega * Math.Sqrt(1 - _zeta * _zeta);
    }

    public override double Ease(double progress)
    {
        if (progress <= 0)
        {
            return 0;
        }
        if (progress >= 1)
        {
            return 1;
        }
        var t = progress;
        var envelope = Math.Exp(-_zeta * _omega * t);
        return 1 - envelope * (Math.Cos(_omegaD * t) + _zeta * _omega / _omegaD * Math.Sin(_omegaD * t));
    }
}
