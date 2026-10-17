using Avalonia;
using Avalonia.Controls;

namespace TKCode.Desktop.Motion;

/// <summary>
/// 逐帧驱动的动画标量。每帧把当前值交给 apply 回调（设透明度、变换、尺寸……）。
/// 动画中途改目标时，从当前值和当前速度继续（弹簧）或从当前值重新补间，所以连续快速切换不会跳变。
/// 动效关闭、或 owner 不在可视树上时直接跳到目标值。
/// </summary>
public sealed class MotionValue
{
    private readonly Visual _owner;
    private readonly Action<double> _apply;
    private MotionCurve? _curve;
    private Action? _onSettled;
    private double _from;
    private double _elapsed;

    public MotionValue(Visual owner, double initial, Action<double> apply)
    {
        _owner = owner;
        _apply = apply;
        Value = initial;
        Target = initial;
    }

    public Visual Owner => _owner;

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; private set; }

    public bool IsAnimating { get; private set; }

    /// <summary>
    /// 动画到 target，结束（静止）后调用 onSettled。被新的 AnimateTo / Snap 打断时，之前的 onSettled 不再调用。
    /// </summary>
    public void AnimateTo(double target, MotionCurve curve, Action? onSettled = null)
    {
        if (!MotionSettings.IsEnabled || TopLevel.GetTopLevel(_owner) is null)
        {
            Snap(target);
            onSettled?.Invoke();
            return;
        }
        if (!IsAnimating && Value.Equals(target))
        {
            Target = target;
            onSettled?.Invoke();
            return;
        }
        Target = target;
        _from = Value;
        _curve = curve;
        _elapsed = 0;
        _onSettled = onSettled;
        if (!IsAnimating)
        {
            IsAnimating = true;
            MotionTicker.Add(this);
        }
    }

    /// <summary>立即设为 value（停止动画，不调用 onSettled）。</summary>
    public void Snap(double value)
    {
        _onSettled = null;
        IsAnimating = false;
        Velocity = 0;
        Target = value;
        Value = value;
        _apply(value);
    }

    /// <summary>推进 dt 秒；返回 false 表示已静止。</summary>
    internal bool Step(double dt)
    {
        if (!IsAnimating)
        {
            return false;
        }
        var done = _curve switch
        {
            SpringCurve spring => StepSpring(spring, dt),
            TweenCurve tween => StepTween(tween, dt),
            _ => true,
        };
        if (done)
        {
            Value = Target;
            Velocity = 0;
            IsAnimating = false;
        }
        _apply(Value);
        if (done)
        {
            var callback = _onSettled;
            _onSettled = null;
            callback?.Invoke();
        }
        return !done;
    }

    /// <summary>owner 离开可视树：直接结束在目标值。</summary>
    internal void Finish()
    {
        if (IsAnimating)
        {
            Step(double.PositiveInfinity);
        }
    }

    private bool StepSpring(SpringCurve spring, double dt)
    {
        if (double.IsInfinity(dt))
        {
            return true;
        }
        var k = spring.Stiffness;
        var c = spring.Damping;
        var x = Value;
        var v = Velocity;
        const double h = 1.0 / 240;
        for (var t = 0.0; t < dt; t += h)
        {
            var step = Math.Min(h, dt - t);
            var a = -k * (x - Target) - c * v;
            v += a * step;
            x += v * step;
        }
        Value = x;
        Velocity = v;
        var range = Math.Max(Math.Abs(Target - _from), 1e-3);
        return Math.Abs(x - Target) < range * 1e-3 + 1e-4 && Math.Abs(v) < range * 1e-2 + 1e-3;
    }

    private bool StepTween(TweenCurve tween, double dt)
    {
        _elapsed += dt;
        var duration = tween.Duration.TotalSeconds;
        var p = duration <= 0 ? 1 : Math.Min(1, _elapsed / duration);
        var previous = Value;
        Value = _from + (Target - _from) * tween.Easing.Ease(p);
        Velocity = dt > 0 && !double.IsInfinity(dt) ? (Value - previous) / dt : 0;
        return p >= 1;
    }
}

/// <summary>
/// 帧时钟：按 TopLevel 合并成一次 RequestAnimationFrame，只在有动画时请求下一帧。
/// 测试可切到手动时钟，用 <see cref="Advance"/> 精确推进。
/// </summary>
public static class MotionTicker
{
    private static readonly List<MotionValue> Active = [];
    private static readonly HashSet<TopLevel> Pending = [];
    private static readonly Dictionary<TopLevel, TimeSpan> LastFrame = [];

    /// <summary>最长单帧步长：卡顿后放慢而不是瞬移。</summary>
    private const double MaxStep = 1.0 / 20;

    /// <summary>手动时钟（测试）：不请求真实帧，只由 Advance 推进。</summary>
    public static bool IsManual { get; set; }

    public static int ActiveCount => Active.Count;

    /// <summary>切回真实时钟时丢弃手动时钟里未触发的延迟。</summary>
    public static void ResetManual()
    {
        Delays.Clear();
        _manualNow = TimeSpan.Zero;
    }

    internal static void Add(MotionValue value)
    {
        Active.Add(value);
        if (!IsManual)
        {
            Request(value.Owner);
        }
    }

    private static readonly List<(TimeSpan Due, Action Action, Handle Handle)> Delays = [];
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static TimeSpan _manualNow;

    /// <summary>动效时间：手动时钟下是累计推进的时间，否则是真实时间。</summary>
    public static TimeSpan Now => IsManual ? _manualNow : Clock.Elapsed;

    /// <summary>延迟执行（错开入场、最短展示时间）：手动时钟下随 Advance 触发，否则用 DispatcherTimer。</summary>
    public static IDisposable Delay(TimeSpan delay, Action action)
    {
        if (!IsManual)
        {
            return Avalonia.Threading.DispatcherTimer.RunOnce(action, delay);
        }
        var handle = new Handle();
        Delays.Add((_manualNow + delay, action, handle));
        return handle;
    }

    private sealed class Handle : IDisposable
    {
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }

    /// <summary>推进所有动画 dt（手动时钟）。</summary>
    public static void Advance(TimeSpan dt)
    {
        // 按帧（16ms）推进，延迟动作在正确的时刻触发
        var frame = TimeSpan.FromMilliseconds(16);
        while (dt > frame)
        {
            AdvanceFrame(frame);
            dt -= frame;
        }
        AdvanceFrame(dt);
    }

    private static void AdvanceFrame(TimeSpan dt)
    {
        _manualNow += dt;
        foreach (var due in Delays.Where(d => d.Due <= _manualNow).ToArray())
        {
            Delays.Remove(due);
            if (!due.Handle.Cancelled)
            {
                due.Action();
            }
        }
        foreach (var value in Active.ToArray())
        {
            if (!value.Step(dt.TotalSeconds))
            {
                Active.Remove(value);
            }
        }
    }

    /// <summary>把所有动画推进到结束。</summary>
    public static void FinishAll()
    {
        for (var i = 0; i < 1000 && (Active.Count > 0 || Delays.Count > 0); i++)
        {
            Advance(TimeSpan.FromMilliseconds(16));
        }
    }

    private static void Request(Visual owner)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top || !Pending.Add(top))
        {
            return;
        }
        top.RequestAnimationFrame(time => OnFrame(top, time));
    }

    private static void OnFrame(TopLevel top, TimeSpan time)
    {
        Pending.Remove(top);
        var dt = LastFrame.TryGetValue(top, out var last) && time > last ? (time - last).TotalSeconds : 1.0 / 60;
        LastFrame[top] = time;
        dt = Math.Min(dt, MaxStep);
        var more = false;
        foreach (var value in Active.ToArray())
        {
            var owner = TopLevel.GetTopLevel(value.Owner);
            if (owner is null)
            {
                value.Finish();
                Active.Remove(value);
                continue;
            }
            if (owner != top)
            {
                continue;
            }
            if (value.Step(dt))
            {
                more = true;
            }
            else
            {
                Active.Remove(value);
            }
        }
        if (more)
        {
            Pending.Add(top);
            top.RequestAnimationFrame(t => OnFrame(top, t));
        }
        else
        {
            // 下一段动画从新的起点计时
            LastFrame.Remove(top);
        }
    }
}
