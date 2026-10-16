using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TCode.Desktop.Motion;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>动效基础件：过程连续、可中途反向、结束状态与无动效时一致。</summary>
public sealed class MotionTests
{
    private static (AtomUI.Desktop.Controls.Window Window, StackPanel Root) CreateWindow()
    {
        var root = new StackPanel { Width = 300, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new AtomUI.Desktop.Controls.Window { Width = 400, Height = 400, Content = root, Background = Brushes.Black };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, root);
    }

    [Fact]
    public async Task Reveal_AnimatesHeight_AndReversesMidway()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var reveal = new Reveal { IsOpen = false, Child = new Border { Height = 100, Background = Brushes.Purple } };
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.False(reveal.Child!.IsVisible);

            reveal.IsOpen = true;
            HeadlessApp.Step(window, 60);
            var h1 = reveal.Bounds.Height;
            Assert.InRange(h1, 1, 99);
            HeadlessApp.Step(window, 60);
            Assert.True(reveal.Bounds.Height > h1);

            // 中途反向：从当前高度往回收，不跳到 0 或 100
            var before = reveal.Bounds.Height;
            reveal.IsOpen = false;
            HeadlessApp.Step(window, 16);
            Assert.InRange(reveal.Bounds.Height, 1, 99);
            Assert.True(Math.Abs(reveal.Bounds.Height - before) < 40);

            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.False(reveal.Child.IsVisible);

            reveal.IsOpen = true;
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(100, reveal.Bounds.Height);
            Assert.Equal(1, reveal.Child.Opacity);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reveal_InitialValue_DoesNotAnimate()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var reveal = new Reveal { Child = new Border { Height = 50 } };
            root.Children.Add(reveal);
            reveal.IsOpen = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, MotionTicker.ActiveCount);
            Assert.Equal(0, reveal.Bounds.Height);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Presence_HidesAfterExit_AndRestoresStyleValues()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var card = new Border { Height = 40, Background = Brushes.Teal };
            Presence.SetEffect(card, PresenceEffect.Pop);
            root.Children.Add(card);
            Dispatcher.UIThread.RunJobs();

            Presence.SetIsPresent(card, false);
            Assert.True(card.IsVisible);
            HeadlessApp.Step(window, 50);
            Assert.InRange(card.Opacity, 0.01, 0.99);
            Assert.NotNull(card.RenderTransform);
            MotionTicker.FinishAll();
            Assert.False(card.IsVisible);

            Presence.SetIsPresent(card, true);
            Assert.True(card.IsVisible);
            HeadlessApp.Step(window, 40);
            Assert.InRange(card.Opacity, 0.01, 0.99);
            MotionTicker.FinishAll();
            Assert.Equal(1, card.Opacity);
            Assert.Null(card.RenderTransform);
            Assert.False(card.IsSet(Visual.OpacityProperty));
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MotionDisabled_SnapsImmediately()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, root) = CreateWindow();
            var card = new Border { Height = 40 };
            var reveal = new Reveal { Child = new Border { Height = 30 } };
            root.Children.Add(card);
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            Presence.SetIsPresent(card, false);
            reveal.IsOpen = false;
            window.UpdateLayout();
            Assert.False(card.IsVisible);
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.Equal(0, MotionTicker.ActiveCount);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SmoothSize_FollowsContentHeight()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var content = new Border { Height = 40 };
            var smooth = new SmoothSize { Child = content };
            root.Children.Add(smooth);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(40, smooth.Bounds.Height);

            content.Height = 120;
            window.UpdateLayout();
            HeadlessApp.Step(window, 50);
            Assert.InRange(smooth.Bounds.Height, 41, 119);
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(120, smooth.Bounds.Height);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Composer_SingleLineToMultiline_MorphsParts()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var composer = new TCode.Desktop.Views.ComposerView { Width = 640, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            composer.Classes.Add("compact");
            var window = new AtomUI.Desktop.Controls.Window { Width = 700, Height = 400, Content = composer, Background = Brushes.Black };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var input = composer.FindControl<TextBox>("Input")!;
            var pickers = composer.FindControl<StackPanel>("Pickers")!;
            var oneLine = composer.Bounds.Height;
            HeadlessApp.Save(window, "motion-composer-0");

            input.Text = string.Concat(Enumerable.Repeat("您好", 120));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("multiline", composer.Classes);
            HeadlessApp.Step(window, 40);
            // 中途：模型选择器带着反向位移从原位置滑过来，外框高度介于单行与两段之间
            Assert.IsType<TranslateTransform>(pickers.RenderTransform);
            HeadlessApp.Save(window, "motion-composer-mid");
            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Null(pickers.RenderTransform);
            Assert.True(composer.Bounds.Height > oneLine + 20);
            HeadlessApp.Save(window, "motion-composer-1");
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Spring_SettlesAndOvershootsSlightly()
    {
        var ease = new SpringEase(0.72);
        Assert.Equal(0, ease.Ease(0));
        Assert.Equal(1, ease.Ease(1));
        var max = Enumerable.Range(0, 100).Select(i => ease.Ease(i / 100.0)).Max();
        Assert.InRange(max, 1.005, 1.08);
    }
}
