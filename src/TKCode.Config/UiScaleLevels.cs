namespace TKCode.Config;

/// <summary>界面缩放（settings.json 的 uiScale）的取值范围与档位：设置页、快捷键与文件校验共用。</summary>
public static class UiScaleLevels
{
    public const double Default = 1.0;
    public const double Min = 0.75;
    public const double Max = 2.0;

    /// <summary>设置页的分段档位，也是 ⌘+ / ⌘- 依次经过的档位。</summary>
    public static IReadOnlyList<double> Stops { get; } = [0.8, 0.9, 1.0, 1.15, 1.3, 1.5];

    private const double Tolerance = 0.005;

    /// <summary>限制在 [Min, Max]；非有限值（NaN、无穷）按默认值处理。</summary>
    public static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;

    public static bool IsDefault(double value) => Same(value, Default);

    public static bool Same(double a, double b) => Math.Abs(a - b) < Tolerance;

    /// <summary>
    /// 沿档位前进或后退一档：<paramref name="direction"/> 大于 0 取比当前大的最小档，小于 0 取比当前小的最大档。
    /// 当前值不在档位上（手改 settings.json）时同样适用；已到头则保持不变。
    /// </summary>
    public static double Step(double current, int direction)
    {
        current = Clamp(current);
        if (direction > 0)
        {
            foreach (var stop in Stops)
            {
                if (stop > current + Tolerance)
                {
                    return stop;
                }
            }
        }
        else if (direction < 0)
        {
            for (var i = Stops.Count - 1; i >= 0; i--)
            {
                if (Stops[i] < current - Tolerance)
                {
                    return Stops[i];
                }
            }
        }
        return current;
    }
}
