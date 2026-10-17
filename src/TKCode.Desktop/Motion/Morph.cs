using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TKCode.Desktop.Motion;

/// <summary>
/// FLIP 变形：布局切换（例如输入框单行 → 两段）前记录各部件相对容器的位置，切换并完成布局后，
/// 给部件加上“新位置 → 旧位置”的反向位移，再弹簧动画回 0。部件看起来是从原处滑到新位置，而不是瞬移。
/// </summary>
public static class Morph
{
    private static readonly AttachedProperty<MotionValue?> RunProperty =
        AvaloniaProperty.RegisterAttached<Control, MotionValue?>("Run", typeof(Morph));

    public static void Run(Control container, IReadOnlyList<Control> parts, Action change)
    {
        if (!MotionSettings.IsEnabled || !container.IsLoaded)
        {
            change();
            return;
        }
        // 正在进行中的变形：从当前视觉位置（含位移）算起
        var before = parts.Select(p => Position(p, container)).ToArray();
        change();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            container.LayoutUpdated -= handler;
            for (var i = 0; i < parts.Count; i++)
            {
                if (before[i] is not { } from || Position(parts[i], container, includeTransform: false) is not { } to)
                {
                    continue;
                }
                var delta = from - to;
                if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5)
                {
                    continue;
                }
                Animate(parts[i], delta);
            }
        };
        container.LayoutUpdated += handler;
    }

    /// <summary>
    /// 共享元素过渡：target 在下一次布局后从 source 当前所在的位置（按左边缘与底边对齐）滑到自己的新位置。
    /// 用于空状态的居中输入框 → 会话中的底部输入框（两个实例，看起来是同一个输入框落到了底部）。
    /// </summary>
    public static void Fly(Control source, Control target, Control container)
    {
        if (!MotionSettings.IsEnabled || !source.IsEffectivelyVisible || source.TranslatePoint(default, container) is not { } from)
        {
            return;
        }
        var fromBottom = from.Y + source.Bounds.Height;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            container.LayoutUpdated -= handler;
            if (target.TranslatePoint(default, container) is not { } to)
            {
                return;
            }
            var delta = new Point(from.X - to.X, fromBottom - (to.Y + target.Bounds.Height));
            if (Math.Abs(delta.X) >= 0.5 || Math.Abs(delta.Y) >= 0.5)
            {
                Animate(target, delta);
            }
        };
        container.LayoutUpdated += handler;
    }

    private static Point? Position(Control part, Visual container, bool includeTransform = true)
    {
        if (!part.IsEffectivelyVisible || part.TranslatePoint(default, container) is not { } point)
        {
            return null;
        }
        if (!includeTransform && part.RenderTransform is TranslateTransform t)
        {
            point -= new Point(t.X, t.Y);
        }
        return point;
    }

    private static void Animate(Control part, Point delta)
    {
        var translate = new TranslateTransform(delta.X, delta.Y);
        part.RenderTransform = translate;
        part.GetValue(RunProperty)?.Snap(0);
        var run = new MotionValue(part, 1, t =>
        {
            translate.X = delta.X * t;
            translate.Y = delta.Y * t;
        });
        part.SetValue(RunProperty, run);
        run.AnimateTo(0, MotionTokens.Glide, () =>
        {
            if (ReferenceEquals(part.RenderTransform, translate))
            {
                part.ClearValue(Visual.RenderTransformProperty);
            }
        });
    }
}
