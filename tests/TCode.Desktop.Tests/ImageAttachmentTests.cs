using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Controls.Composer;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 图片附件：保存到客户端附件目录，输入框显示缩略图与占位符；发送时占位符展开为带路径的引用，
/// 消息气泡显示图片并把引用折叠回占位符；会话记录只保存路径（不含 base64）。
/// 粘贴本身依赖系统剪贴板，这里直接走与粘贴相同的保存与添加路径。
/// </summary>
public sealed class ImageAttachmentTests
{
    [Fact]
    public void Expand_ReplacesPlaceholders_AndAppendsUnreferencedImages()
    {
        var a = new ImageAttachmentViewModel("/tmp/a.png", "image/png", 1);
        var b = new ImageAttachmentViewModel("/tmp/b.png", "image/png", 2);
        var c = new ImageAttachmentViewModel("/tmp/c.png", "image/png", 3);
        var text = ImagePrompt.Expand("对比 [图片 #1] 和 [Image #2]，忽略 [图片 #9]", [a, b, c]);
        Assert.Equal("对比 [Image #1: /tmp/a.png] 和 [Image #2: /tmp/b.png]，忽略 [图片 #9]\n[Image #3: /tmp/c.png]", text);
        Assert.Equal("[Image #1: /tmp/a.png]", ImagePrompt.Expand("", [a]));
        // 移除后再添加造成的编号空缺：按附件顺序重新编号
        Assert.Equal("[Image #1: /tmp/a.png] [Image #2: /tmp/c.png]", ImagePrompt.Expand("[图片 #1] [图片 #3]", [a, c]));
        Assert.DoesNotContain("/tmp/", ImagePrompt.Collapse(text));
    }

    [Fact]
    public async Task PastedImages_ShowInComposerAndMessage_AndSessionStoresPathsOnly()
    {
        var home = Directory.CreateTempSubdirectory("tcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tcode-ui-project-").FullName;
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(project);
        settings.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");

            // 两张“截图”：与粘贴相同，经附件目录保存
            var first = SaveTestImage(Colors.SteelBlue, Colors.MediumPurple);
            var second = SaveTestImage(Colors.DarkOrange, Colors.Crimson);
            Assert.StartsWith(Path.Combine(home, "attachments"), first);
            var conversation = vm.Conversation;
            var a = conversation.AddAttachment(first, "image/png");
            var b = conversation.AddAttachment(second, "image/png");
            conversation.Composer.SetContent([new TextSegment("对比 "), a.Tag, new TextSegment(" 和 "), b.Tag, new TextSegment(" 的配色差异")]);
            await Settle();
            var composer = window.GetVisualDescendants().OfType<ComposerView>().Single(c => c.Classes.Contains("compact"));
            Assert.Contains("multiline", composer.Classes);
            HeadlessApp.Save(window, "composer-images");

            // 移除第二张：图片 tag 一并去掉
            conversation.RemoveAttachmentCommand.Execute(b);
            Assert.Equal("对比 [图片 #1] 和  的配色差异", conversation.ComposerText);
            b = conversation.AddAttachment(second, "image/png");
            conversation.Composer.SetContent([new TextSegment("对比 "), a.Tag, new TextSegment(" 和 "), b.Tag, new TextSegment(" 的配色差异")]);

            conversation.SendCommand.Execute(null);
            Assert.Empty(conversation.Attachments);
            var session = conversation.Active!;
            await WaitUntil(() => session is { IsRunning: false } && session.Items.OfType<UserMessageItemViewModel>().Count() == 2
                && session.Items.Last() is AssistantMessageItemViewModel, "image turn finished");
            var user = session.Items.OfType<UserMessageItemViewModel>().Last();
            Assert.Equal([first, second], user.Images.Select(i => i.Path));
            Assert.Contains($"[Image #1: {first}]", user.Text);
            Assert.Equal("对比 [图片 #1] 和 [图片 #2] 的配色差异", user.DisplayText);
            await Settle();
            HeadlessApp.Save(window, "message-images");

            // 点击消息里的图片：全屏查看器出现在窗口的浮层上
            TCode.Desktop.Controls.ImageOpener.Open(window, user.Images[0]);
            await Settle();
            var viewer = window.GetVisualDescendants().OfType<StreamMark.Avalonia.Rendering.ImageViewer>().SingleOrDefault();
            Assert.NotNull(viewer);
            // 查看器铺满窗口（AtomUI 窗口的覆盖层首次使用前尺寸为 0）
            Assert.Equal(window.Bounds.Width, viewer!.Bounds.Width);
            Assert.Equal(window.Bounds.Height, viewer.Bounds.Height);
            HeadlessApp.Save(window, "message-image-viewer");
            ((Avalonia.Controls.Panel)viewer!.Parent!).Children.Remove(viewer);
            await Settle();

            // 会话记录只保存路径
            var record = await File.ReadAllTextAsync(session.FilePath);
            Assert.Contains(first.Replace("\\", "\\\\"), record);
            Assert.DoesNotContain(Convert.ToBase64String(File.ReadAllBytes(first))[..64], record);

            // 拖放：把位图拖到消息区域，转发给输入框——经过时高亮，放下后成为附件
            var docked = window.FindControl<ComposerView>("DockedComposer")!;
            var transcript = window.FindControl<StreamMark.Avalonia.Transcript.TranscriptView>("Transcript")!;
            using var dragged = new Bitmap(first);
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.SetBitmap(dragged);
            data.Add(item);
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, transcript, new Point(400, 300), KeyModifiers.None);
            transcript.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            Assert.Contains("drop-target", docked.Classes);
            await Settle();
            HeadlessApp.Save(window, "composer-drop-target");
            transcript.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, transcript, new Point(400, 300), KeyModifiers.None));
            await WaitUntil(() => conversation.Attachments.Count == 1, "dropped bitmap attached");
            Assert.DoesNotContain("drop-target", docked.Classes);
            Assert.Equal("[图片 #1] ", conversation.ComposerText);
            Assert.StartsWith(Path.Combine(home, "attachments"), conversation.Attachments[0].Path);

            // 拖入的不是图片（纯文本）：不拦截
            var text = new DataTransfer();
            var textItem = new DataTransferItem();
            textItem.SetText("hello");
            text.Add(textItem);
            var textOver = new DragEventArgs(DragDrop.DragOverEvent, text, transcript, new Point(400, 300), KeyModifiers.None);
            transcript.RaiseEvent(textOver);
            Assert.False(textOver.Handled);
            Assert.DoesNotContain("drop-target", docked.Classes);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static string SaveTestImage(Color from, Color to)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 400));
        using (var context = bitmap.CreateDrawingContext())
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
            };
            context.FillRectangle(brush, new Rect(0, 0, 640, 400));
            context.FillRectangle(Brushes.White, new Rect(60, 60, 260, 40), 8);
            context.FillRectangle(new SolidColorBrush(Colors.White, 0.6), new Rect(60, 130, 400, 24), 6);
            context.FillRectangle(new SolidColorBrush(Colors.White, 0.6), new Rect(60, 170, 340, 24), 6);
        }
        return AttachmentStore.SaveBitmap(bitmap);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
