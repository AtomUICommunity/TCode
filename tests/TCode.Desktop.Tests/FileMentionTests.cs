using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Controls.Composer;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using TCode.Rpc;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>@ 文件（docs/design/composer.md §8）：经由真实 Host 的文件索引搜索，选中后成为文件 tag，Tab 进入文件夹，发送时只发路径。</summary>
public sealed class FileMentionTests
{
    [Fact]
    public async Task AtFile_SearchesWorkspace_InsertsTag_AndSendsPathOnly()
    {
        var home = Directory.CreateTempSubdirectory("tcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tcode-ui-project-").FullName;
        Write(project, "src/Views/ComposerView.axaml.cs");
        Write(project, "src/Views/MainWindow.axaml");
        Write(project, "src/Controls/Composer/ComposerEditor.cs");
        Write(project, "docs/design/composer.md");
        Write(project, "README.md");
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
            var conversation = vm.Conversation;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => conversation.Picker.Selected is not null && conversation.CurrentCwd is not null, "models");
            var emptyComposer = window.FindControl<ComposerView>("EmptyComposer")!;
            var dockedComposer = window.FindControl<ComposerView>("DockedComposer")!;
            // 截图需要弹层画在窗口的浮层上
            foreach (var popup in new[] { emptyComposer, dockedComposer }.Select(c => c.FindControl<TCode.Desktop.Motion.Popover>("CompletionPopup")!))
            {
                popup.ShouldUseOverlayLayer = true;
            }
            var editor = emptyComposer.FindControl<ComposerEditor>("Input")!;
            editor.Focus();

            var heightBefore = editor.Bounds.Height;
            // 空查询：浅层条目（文件夹在前）
            window.KeyTextInputText("看下 @");
            await WaitUntil(() => conversation.Completion.Items.Count > 0, "shallow listing");
            Assert.Equal(["docs/", "src/"], conversation.Completion.Items.Take(2).Select(i => i.Title));

            // 模糊搜索：文件名命中高亮，目录显示为副标题
            window.KeyTextInputText("compview");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "ComposerView.axaml.cs", "search results");
            var first = conversation.Completion.Items[0];
            // 弹层只在获得焦点的输入框上打开，位于 @ 的上方，不改变输入框的高度
            var popupOpen = emptyComposer.FindControl<TCode.Desktop.Motion.Popover>("CompletionPopup")!;
            Assert.True(popupOpen.IsOpen);
            Assert.False(dockedComposer.FindControl<TCode.Desktop.Motion.Popover>("CompletionPopup")!.IsOpen);
            var list = emptyComposer.FindControl<ItemsControl>("CompletionList")!;
            var listBottomLeft = list.TranslatePoint(new Point(0, list.Bounds.Height), editor)!.Value;
            Assert.True(listBottomLeft.Y <= editor.CaretBounds(3).Top + 1);
            // 横向对齐到 @
            Assert.InRange(listBottomLeft.X - editor.CaretBounds(3).X, -20, 0);
            Assert.Equal(heightBefore, editor.Bounds.Height);
            Assert.Equal("src/Views", first.Subtitle);
            Assert.NotEmpty(first.Highlights);
            HeadlessApp.Save(window, "composer-at-file");

            Assert.True(conversation.Completion.AcceptHighlighted());
            await WaitUntil(() => !popupOpen.IsOpen, "popup closed");
            Assert.Equal(new TagSegment(TagKind.File, "src/Views/ComposerView.axaml.cs", "ComposerView.axaml.cs"), conversation.Composer.TagAt(3));
            Assert.Equal(conversation.Composer.Length, editor.CaretIndex);

            // 文件夹：Tab 进入继续搜索，回车插入文件夹 tag
            window.KeyTextInputText("和 @contr");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "Controls/", "folder result");
            Assert.True(conversation.Completion.TabComplete());
            Assert.EndsWith("@src/Controls/", conversation.ComposerText);
            await WaitUntil(() => conversation.Completion.Items.Any(i => i.Title == "Composer/"), "folder contents");
            window.KeyTextInputText("Composer/");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "Composer/", "nested folder");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Equal("看下 @src/Views/ComposerView.axaml.cs 和 @src/Controls/Composer/ ", conversation.ComposerText);
            HeadlessApp.Save(window, "composer-file-tags");

            // 发送：只发路径；最近使用的文件被记住
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "turn finished");
            var user = conversation.Active!.Items.OfType<UserMessageItemViewModel>().Single();
            Assert.Equal("看下 @src/Views/ComposerView.axaml.cs 和 @src/Controls/Composer/", user.Text);
            Assert.Equal(["src/Controls/Composer/", "src/Views/ComposerView.axaml.cs"],
                new StateService(Path.Combine(home, "state.json")).Current.RecentMentions.Values.Single());

            // 空查询时最近使用的排在最前
            var docked = dockedComposer.FindControl<ComposerEditor>("Input")!;
            docked.Focus();
            window.KeyTextInputText("@");
            await WaitUntil(() => conversation.Completion.Items.Count > 0, "recent listing");
            Assert.Equal("Composer/", conversation.Completion.Items[0].Title);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
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
            await Task.Delay(30);
        }
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}

internal static class HeadlessTyping
{
    /// <summary>逐字输入（每个字符一次文本输入事件，像键盘输入一样触发补全）。</summary>
    public static void KeyTextInputText(this Avalonia.Controls.Window window, string text)
    {
        foreach (var c in text)
        {
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }
}
