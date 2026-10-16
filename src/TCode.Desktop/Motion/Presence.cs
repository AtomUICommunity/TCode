using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace TCode.Desktop.Motion;

public enum PresenceEffect
{
    Fade,
    /// <summary>缩放弹出（0.92 → 1，带过冲）。</summary>
    Pop,
    /// <summary>从下方上移出现。</summary>
    SlideUp,
    /// <summary>从上方下移出现。</summary>
    SlideDown,
    /// <summary>从右侧左移出现。</summary>
    SlideLeft,
    /// <summary>从左侧右移出现。</summary>
    SlideRight,
    /// <summary>轻微缩放（0.97 → 1，浮层卡片、页面）。</summary>
    Zoom,
}

/// <summary>
/// 出现 / 消失（不改变占位的元素）：替代 IsVisible 绑定，写成 motion:Presence.IsPresent="{Binding …}"。
/// 只动透明度与渲染变换，不触发布局；消失动画播完才真正隐藏。
/// 默认值为 true：绑定求值为 false 时才会触发变化并隐藏。
/// </summary>
public static class Presence
{
    public static readonly AttachedProperty<bool> IsPresentProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsPresent", typeof(Presence), defaultValue: true);

    public static readonly AttachedProperty<PresenceEffect> EffectProperty =
        AvaloniaProperty.RegisterAttached<Control, PresenceEffect>("Effect", typeof(Presence), PresenceEffect.Fade);

    /// <summary>位移距离（Slide*）。</summary>
    public static readonly AttachedProperty<double> DistanceProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Distance", typeof(Presence), 8);

    /// <summary>出现前的延迟（错开入场）。</summary>
    public static readonly AttachedProperty<TimeSpan> DelayProperty =
        AvaloniaProperty.RegisterAttached<Control, TimeSpan>("Delay", typeof(Presence));

    /// <summary>
    /// false：消失时立即隐藏，只播出现动画（同一个 StackPanel 里互斥切换的分区：新旧内容不会同时占位）。
    /// </summary>
    public static readonly AttachedProperty<bool> AnimateExitProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("AnimateExit", typeof(Presence), true);

    private static readonly AttachedProperty<Controller?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<Control, Controller?>("Controller", typeof(Presence));

    static Presence()
    {
        IsPresentProperty.Changed.AddClassHandler<Control>((control, e) => GetController(control).Update(e.GetNewValue<bool>()));
    }

    public static bool GetIsPresent(Control control) => control.GetValue(IsPresentProperty);
    public static void SetIsPresent(Control control, bool value) => control.SetValue(IsPresentProperty, value);
    public static PresenceEffect GetEffect(Control control) => control.GetValue(EffectProperty);
    public static void SetEffect(Control control, PresenceEffect value) => control.SetValue(EffectProperty, value);
    public static double GetDistance(Control control) => control.GetValue(DistanceProperty);
    public static void SetDistance(Control control, double value) => control.SetValue(DistanceProperty, value);
    public static bool GetAnimateExit(Control control) => control.GetValue(AnimateExitProperty);
    public static void SetAnimateExit(Control control, bool value) => control.SetValue(AnimateExitProperty, value);
    public static TimeSpan GetDelay(Control control) => control.GetValue(DelayProperty);
    public static void SetDelay(Control control, TimeSpan value) => control.SetValue(DelayProperty, value);

    /// <summary>代码中直接播放一次入场（列表新增项）。</summary>
    public static void PlayEnter(Control control, PresenceEffect effect, TimeSpan delay = default)
    {
        var controller = GetController(control);
        controller.Enter(effect, delay);
    }

    private static Controller GetController(Control control)
    {
        if (control.GetValue(ControllerProperty) is not { } controller)
        {
            controller = new Controller(control);
            control.SetValue(ControllerProperty, controller);
        }
        return controller;
    }

    /// <summary>按进度 p（0 消失 … 1 出现）设置透明度与变换；p=1 时清掉本地值，交还给样式。</summary>
    internal static void Apply(Control control, PresenceEffect effect, double distance, double p)
    {
        if (p >= 1 && Math.Abs(p - 1) < 1e-6)
        {
            control.ClearValue(Visual.OpacityProperty);
            control.ClearValue(Visual.RenderTransformProperty);
            return;
        }
        control.Opacity = Math.Clamp(p, 0, 1);
        var q = 1 - p;
        var builder = new TransformOperations.Builder(2);
        switch (effect)
        {
            case PresenceEffect.Pop:
                builder.AppendScale(1 - 0.08 * q, 1 - 0.08 * q);
                break;
            case PresenceEffect.Zoom:
                builder.AppendScale(1 - 0.03 * q, 1 - 0.03 * q);
                builder.AppendTranslate(0, -distance * q);
                break;
            case PresenceEffect.SlideUp:
                builder.AppendTranslate(0, distance * q);
                break;
            case PresenceEffect.SlideDown:
                builder.AppendTranslate(0, -distance * q);
                break;
            case PresenceEffect.SlideLeft:
                builder.AppendTranslate(distance * q, 0);
                break;
            case PresenceEffect.SlideRight:
                builder.AppendTranslate(-distance * q, 0);
                break;
            default:
                return;
        }
        control.RenderTransform = builder.Build();
    }

    private sealed class Controller
    {
        private readonly Control _control;
        private readonly MotionGate _gate;
        private readonly MotionValue _progress;
        private PresenceEffect _effect;
        private IDisposable? _delay;

        public Controller(Control control)
        {
            _control = control;
            _gate = new MotionGate(control);
            _effect = GetEffect(control);
            _progress = new MotionValue(control, control.IsVisible ? 1 : 0,
                p => Apply(_control, _effect, GetDistance(_control), p));
        }

        public void Update(bool present)
        {
            _delay?.Dispose();
            _delay = null;
            _effect = GetEffect(_control);
            if (!_gate.ShouldAnimate())
            {
                _progress.Snap(present ? 1 : 0);
                _control.IsVisible = present;
                return;
            }
            if (present)
            {
                if (!_control.IsVisible)
                {
                    _progress.Snap(0);
                    _control.IsVisible = true;
                }
                Start(1, MotionTokens.Bouncy, GetDelay(_control), null);
            }
            else if (!GetAnimateExit(_control))
            {
                _progress.Snap(0);
                _control.IsVisible = false;
            }
            else
            {
                _progress.AnimateTo(0, MotionTokens.Exit, () => _control.IsVisible = false);
            }
        }

        public void Enter(PresenceEffect effect, TimeSpan delay)
        {
            if (!MotionSettings.IsEnabled)
            {
                return;
            }
            _effect = effect;
            _progress.Snap(0);
            Start(1, MotionTokens.Bouncy, delay, null);
        }

        private void Start(double target, MotionCurve curve, TimeSpan delay, Action? done)
        {
            if (delay <= TimeSpan.Zero)
            {
                _progress.AnimateTo(target, curve, done);
                return;
            }
            _delay = MotionTicker.Delay(delay, () =>
            {
                _delay = null;
                _progress.AnimateTo(target, curve, done);
            });
        }
    }
}
