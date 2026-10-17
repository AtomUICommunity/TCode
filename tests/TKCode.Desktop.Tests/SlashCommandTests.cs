using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>/ 命令（docs/design/composer.md §9）：内置命令在界面执行；自定义 command 成为 tag，发送时 Host 展开 .md 正文，转录按输入框的样子显示。</summary>
public sealed class SlashCommandTests
{
    [Fact]
    public async Task CustomCommand_ExpandsOnHost_AndShowsAsTagInTranscript()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Write(project, ".agents/commands/review.md", "---\ndescription: Review the diff\n---\nReview the uncommitted changes carefully.\n");
        Write(home, "commands/review.md", "Global review (overridden by the project).");
        Write(home, "commands/git/commit.md", "Write a commit message.");
        Write(home, "commands/new.md", "Shadowed by the built-in /new.");
        Write(project, "src/App.cs", "");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
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
            foreach (var popup in window.GetVisualDescendants().OfType<Popup>())
            {
                popup.ShouldUseOverlayLayer = true;
            }
            var editor = window.FindControl<ComposerView>("EmptyComposer")!.FindControl<ComposerEditor>("Input")!;
            editor.Focus();

            // 空查询：模式（当前的 Agent 不列出）、内置命令、自定义 command；与内置重名的被遮住
            window.KeyTextInputText("/");
            await WaitUntil(() => conversation.Completion.Items.Any(i => i.Title == "git:commit"), "slash listing");
            var titles = conversation.Completion.Items.Select(i => i.Title).ToList();
            Assert.Equal(["plan", "acp", "new", "model"], titles.Take(4));
            Assert.DoesNotContain("fork", titles);   // 还没有会话
            Assert.Single(titles, t => t == "new");
            var review = conversation.Completion.Items.Single(i => i.Title == "review");
            Assert.Equal(("Review the diff", "项目"), (review.Subtitle, review.Source));
            HeadlessApp.Save(window, "composer-slash-all");   // 列表出现滚动条：右侧来源列不被盖住

            window.KeyTextInputText("rev");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "review", "filtered");
            HeadlessApp.Save(window, "composer-slash");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Equal(new TagSegment(TagKind.Command, "review", "review"), conversation.Composer.TagAt(0));
            window.KeyTextInputText("重点看测试");

            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "turn finished");
            var session = conversation.Active!;
            var user = session.Items.OfType<UserMessageItemViewModel>().Single();
            // 模型收到的是展开后的正文 + 用户文字
            Assert.Equal("Review the uncommitted changes carefully.\n\n重点看测试", user.Text);
            Assert.True(user.HasSegments);
            Assert.Equal("/review 重点看测试", user.Segments!.ToPlainText());
            Assert.Equal("/review 重点看测试", session.Title);
            await WaitUntil(() => window.GetVisualDescendants().OfType<ComposerEditor>().Any(e => e.IsReadOnly && e.IsEffectivelyVisible), "tag bubble");
            HeadlessApp.Save(window, "message-command-tag");

            // 会话记录保留显示用的片段：重新打开后仍按 tag 显示
            var record = await File.ReadAllTextAsync(session.FilePath);
            Assert.Contains("\"display\"", record);
            var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
            var reopened = await server.OpenSessionAsync(session.FilePath);
            var history = reopened.History.Single(m => m.Role == "user");
            Assert.Equal(["command", "text"], history.Segments!.Select(s => s.Kind));

            // 外部运行时报告的命令：出现在 / 列表，选中后为运行时命令 tag，原样以 /名称 发送
            // （外部运行时的会话不提供内置 /compact，运行时自己的同名命令不会被遮住）
            session.Runtime = "claude-code";
            session.Apply(new TKCode.Rpc.CommandsChanged([new TKCode.Rpc.RuntimeCommandDto("compact", "Summarize the conversation", "<instructions>")]));
            conversation.Composer.SetContent([new TextSegment("/compa")]);
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "compact", "runtime command");
            Assert.Equal("<instructions>  Summarize the conversation", conversation.Completion.Items[0].Subtitle);
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Equal(TagKind.RuntimeCommand, conversation.Composer.TagAt(0)!.Kind);
            Assert.Equal("/compact ", ComposerSegments.ModelText(conversation.Composer.Segments));
            conversation.Composer.SetContent([]);

            // /fork 在有会话后出现；/model 打开当前输入框的模型选择器
            var docked = window.FindControl<ComposerView>("DockedComposer")!;
            docked.FindControl<ComposerEditor>("Input")!.Focus();
            window.KeyTextInputText("/mod");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "model", "model command");
            Assert.True(conversation.Completion.AcceptHighlighted());
            await WaitUntil(() => docked.FindControl<ModelPickerView>("Picker")!.GetVisualDescendants().OfType<Popup>().Single().IsOpen, "model picker");
            Assert.Equal("", conversation.ComposerText);
            docked.FindControl<ModelPickerView>("Picker")!.Toggle();

            // 找不到的 command：发送失败，输入框恢复
            conversation.Composer.SetContent([new TagSegment(TagKind.Command, "missing", "missing"), new TextSegment(" x")]);
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.StatusMessage is not null, "send error");
            Assert.Contains("/missing", conversation.StatusMessage);
            Assert.Equal("/missing x", conversation.ComposerText);

            // /new：回到空会话
            conversation.Composer.SetContent([]);
            window.KeyTextInputText("/new");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "new", "new command");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Null(conversation.Active);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Skills_ExplicitTagAttachesInstructions_AndModelCanLoadThem()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Write(home, "skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Read PDF files\n---\nUse scripts/extract.py to read PDFs.\n");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
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
            foreach (var popup in window.GetVisualDescendants().OfType<Popup>())
            {
                popup.ShouldUseOverlayLayer = true;
            }
            window.FindControl<ComposerView>("EmptyComposer")!.FindControl<ComposerEditor>("Input")!.Focus();

            // 显式调用：/pdf 成为 skill tag，发送时 SKILL.md 附在用户文字之前，名字仍留在文字里
            window.KeyTextInputText("用 /pd");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "pdf", "skill listing");
            var item = conversation.Completion.Items[0];
            Assert.Equal(("Read PDF files", "全局"), (item.Subtitle, item.Source));
            HeadlessApp.Save(window, "composer-slash-skill");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Equal(TagKind.Skill, conversation.Composer.TagAt(2)!.Kind);
            window.KeyTextInputText("读一下 a.pdf");
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "turn finished");
            var session = conversation.Active!;
            var user = session.Items.OfType<UserMessageItemViewModel>().Single();
            Assert.StartsWith($"<skill name=\"pdf\" directory=\"{Path.Combine(home, "skills", "pdf")}\">\nUse scripts/extract.py to read PDFs.\n</skill>\n\n用 /pdf 读一下 a.pdf",
                user.Text);
            Assert.Equal("用 /pdf 读一下 a.pdf", user.Segments!.ToPlainText());

            // 自动发现：系统提示列出 skill，模型调用 skill 工具读取说明
            var record = await File.ReadAllTextAsync(session.FilePath);
            Assert.Contains("- pdf: Read PDF files", record);
            conversation.ComposerText = "mcp:skill {\"name\":\"pdf\"}";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => !session.IsRunning && Tool(session, "skill")?.Result is { Length: > 0 }, "skill tool");
            var call = Tool(session, "skill")!;
            Assert.Contains("Use scripts/extract.py", call.Result);
            Assert.True(call.IsSucceeded);
            Assert.Equal(TKCode.Desktop.Icons.LucideKind.Sparkles, call.Icon);
            await WaitUntil(() => true, "render");
            HeadlessApp.Save(window, "message-skill-tool");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static ToolCallBlockViewModel? Tool(SessionViewModel session, string name) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks)
            .Concat(session.Items.OfType<ActivityGroupViewModel>().SelectMany(g => g.Steps))
            .OfType<ToolCallBlockViewModel>().LastOrDefault(t => t.ToolName == name);

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
