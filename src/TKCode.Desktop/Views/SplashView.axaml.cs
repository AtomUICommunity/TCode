using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Motion;

namespace TKCode.Desktop.Views;

/// <summary>
/// 启动加载页。<see cref="Dismiss"/> 在后台初始化完成后调用：至少展示 <see cref="MinimumDisplay"/>（让第一句歇后语讲完），
/// 然后整页放大淡出。动效关闭时立即消失。
/// </summary>
public partial class SplashView : UserControl
{
    private static readonly TKCodeLangResourceKind[] Quips =
    [
        TKCodeLangResourceKind.SplashQuip1, TKCodeLangResourceKind.SplashQuip2, TKCodeLangResourceKind.SplashQuip3,
        TKCodeLangResourceKind.SplashQuip4, TKCodeLangResourceKind.SplashQuip5, TKCodeLangResourceKind.SplashQuip6,
        TKCodeLangResourceKind.SplashQuip7, TKCodeLangResourceKind.SplashQuip8, TKCodeLangResourceKind.SplashQuip9,
        TKCodeLangResourceKind.SplashQuip10,
    ];

    public static readonly TimeSpan MinimumDisplay = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan RotateInterval = TimeSpan.FromMilliseconds(3400);

    private readonly int[] _order;
    private readonly DispatcherTimer _rotate = new() { Interval = RotateInterval };
    private readonly MotionValue _quipFade;
    private readonly MotionValue _exit;
    private int _index;
    private TimeSpan _shownAt = MotionTicker.Now;
    private bool _dismissed;

    /// <summary>退场动画开始（主界面此时入场）。</summary>
    public event Action? Dismissing;

    public SplashView()
    {
        InitializeComponent();
        _order = Enumerable.Range(0, Quips.Length).OrderBy(_ => Random.Shared.Next()).ToArray();
        _quipFade = new MotionValue(Quip, 1, v => Quip.Opacity = v);
        _exit = new MotionValue(this, 0, ApplyExit);
        _rotate.Tick += (_, _) => NextQuip();
        Strings.LanguageChanged += (_, _) => ShowQuip(animate: false);
        ShowQuip(animate: false);
        Loaded += (_, _) =>
        {
            _shownAt = MotionTicker.Now;
            if (!MotionSettings.IsEnabled)
            {
                return;
            }
            Logo.Classes.Add("alive");
            Halo.Classes.Add("alive");
            // 入场：Logo 弹出，字标、加载行、歇后语依次上移出现
            Presence.PlayEnter(LogoBox, PresenceEffect.Pop);
            Presence.PlayEnter(Wordmark, PresenceEffect.SlideUp, TimeSpan.FromMilliseconds(120));
            Presence.PlayEnter(LoadingRow, PresenceEffect.SlideUp, TimeSpan.FromMilliseconds(200));
            ShowQuip(animate: true, delay: TimeSpan.FromMilliseconds(320));
            _rotate.Start();
        };
        Unloaded += (_, _) => _rotate.Stop();
    }

    public bool IsDismissed => _dismissed;

    /// <summary>后台初始化完成：满足最短展示时间后退场。</summary>
    public void Dismiss()
    {
        if (_dismissed)
        {
            return;
        }
        _dismissed = true;
        var remaining = MinimumDisplay - (MotionTicker.Now - _shownAt);
        if (!MotionSettings.IsEnabled || remaining <= TimeSpan.Zero)
        {
            Exit();
        }
        else
        {
            MotionTicker.Delay(remaining, Exit);
        }
    }

    private void Exit()
    {
        _rotate.Stop();
        IsHitTestVisible = false;
        Dismissing?.Invoke();
        _exit.AnimateTo(1, MotionTokens.Gentle, () =>
        {
            IsVisible = false;
            Logo.Classes.Remove("alive");
            Halo.Classes.Remove("alive");
        });
    }

    /// <summary>退场：整页轻微放大并淡出，像幕布升起。</summary>
    private void ApplyExit(double p)
    {
        Opacity = Math.Clamp(1 - p, 0, 1);
        var builder = new TransformOperations.Builder(1);
        builder.AppendScale(1 + 0.06 * p, 1 + 0.06 * p);
        Stage.RenderTransform = builder.Build();
    }

    private void NextQuip()
    {
        _index = (_index + 1) % _order.Length;
        _quipFade.AnimateTo(0, MotionTokens.FadeOut, () =>
        {
            _quipFade.Snap(1);
            ShowQuip(animate: true);
        });
    }

    private void ShowQuip(bool animate, TimeSpan delay = default)
    {
        var text = Strings.Get(Quips[_order[_index]]);
        var (setup, dash, punch) = Split(text);
        QuipSetup.Text = setup;
        QuipDash.Text = dash;
        QuipPunch.Text = punch;
        if (!animate || !MotionSettings.IsEnabled)
        {
            return;
        }
        // 前半句先出现，停顿一下再揭晓后半句
        Presence.PlayEnter(QuipSetup, PresenceEffect.SlideUp, delay);
        Presence.PlayEnter(QuipDash, PresenceEffect.Fade, delay + TimeSpan.FromMilliseconds(380));
        Presence.PlayEnter(QuipPunch, PresenceEffect.Pop, delay + TimeSpan.FromMilliseconds(700));
    }

    /// <summary>中文“前半句——后半句”，英文“setup — punchline”。</summary>
    internal static (string Setup, string Dash, string Punch) Split(string text)
    {
        foreach (var separator in new[] { "——", " — " })
        {
            var i = text.IndexOf(separator, StringComparison.Ordinal);
            if (i > 0)
            {
                return (text[..i].Trim(), separator.Trim(), text[(i + separator.Length)..].Trim());
            }
        }
        return (text, "", "");
    }
}
