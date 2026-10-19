using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 内置 ask_user 端到端（docs/design/ask-user.md §7）：真实 Host + Fake 模型（“mcp:ask_user {…}” 触发调用）。
/// 问答面板替换输入框，键盘回答两个问题（单选、多选 + 其他），转录里留下问答卡片；Esc 拒绝回答；Stop 收起面板。
/// </summary>
public sealed class QuestionPanelTests
{
    private const string Ask = """mcp:ask_user {"questions":[{"question":"Which database should we use?","header":"Database","options":[{"label":"Postgres (Recommended)","description":"Full featured, needs a server"},{"label":"SQLite","description":"Zero setup, single file"}]},{"question":"Which features should be included?","header":"Features","multiSelect":true,"options":[{"label":"Auth","description":"Sign in and sessions"},{"label":"Audit log","description":"Who changed what"}]}]}""";

    [Fact]
    public async Task AskUser_PanelReplacesComposer_KeyboardAnswers_DeclineAndStop()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ask-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ask-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Current.LastWorkspace = project;
        state.Save();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 820 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            var conversation = vm.Conversation;
            var panel = window.FindControl<QuestionPanelView>("QuestionPanel")!;
            var composer = window.FindControl<ComposerView>("DockedComposer")!;

            // 1. 提问：面板替换输入框，发送不可用，侧边栏橙点亮起
            conversation.ComposerText = Ask;
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => conversation.IsAnsweringQuestion, "question shown", 30_000);
            var session = conversation.Active!;
            var question = session.CurrentQuestion!;
            Assert.Equal("TKCode 提问", question.Title);
            Assert.True(panel.IsVisible);
            Assert.False(composer.IsVisible);
            Assert.True(conversation.Sessions.Single(s => s.SessionId == session.SessionId).IsWaitingApproval);
            conversation.ComposerText = "draft kept";
            Assert.False(conversation.SendCommand.CanExecute(null));
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused");
            HeadlessApp.Save(window, "question-panel");

            // 2. 单选：数字键选中并前进到下一题
            window.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, "2");
            await Settle();
            Assert.Equal(1, question.CurrentIndex);
            Assert.Equal(["SQLite"], question.Pages[0].ToAnswer().Selected);

            // 3. 多选：数字键切换；“其他”行进入文本框输入；Esc 退出文本框；Enter 到提交页
            window.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            window.KeyPress(Key.D3, RawInputModifiers.None, PhysicalKey.Digit3, "3");
            await WaitUntil(() => panel.FindControl<Avalonia.Controls.TextBox>("OtherInput") is { IsKeyboardFocusWithin: true }
                || panel.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().Any(t => t.IsKeyboardFocusWithin), "other input focused");
            window.KeyTextInput("exports");
            await Settle();
            Assert.Equal("exports", question.Pages[1].OtherText);
            HeadlessApp.Save(window, "question-panel-multi");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Settle();
            Assert.False(question.IsDecided);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Settle();
            Assert.True(question.IsReview);
            HeadlessApp.Save(window, "question-panel-review");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            await WaitUntil(() => !session.IsRunning && Tool(session)?.Status == ToolCallStatus.Succeeded, "answered turn finished", 30_000);
            Assert.False(conversation.IsAnsweringQuestion);
            Assert.True(composer.IsVisible);
            Assert.Equal("draft kept", conversation.ComposerText);
            var call = Tool(session)!;
            Assert.Contains("\"Which database should we use?\"=\"SQLite\"", call.Result);
            Assert.Contains("\"Which features should be included?\"=\"Auth, exports\"", call.Result);
            Assert.Contains("\"status\":\"answered\"", call.DetailsJson);
            Assert.False(conversation.Sessions.Single(s => s.SessionId == session.SessionId).IsWaitingApproval);
            await Settle();
            HeadlessApp.Save(window, "question-card");

            // 4. Esc：拒绝回答，模型收到“结束本轮”
            conversation.ComposerText = Ask;
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => conversation.IsAnsweringQuestion, "second question", 30_000);
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused again");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await WaitUntil(() => !session.IsRunning && Tool(session)?.Result?.StartsWith("The user declined to answer these questions.", StringComparison.Ordinal) == true, "declined", 30_000);
            Assert.Contains("\"status\":\"declined\"", Tool(session)!.DetailsJson);

            // 5. Stop：Host 撤回请求，面板收起，输入框回来
            conversation.ComposerText = Ask;
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => conversation.IsAnsweringQuestion, "third question", 30_000);
            await conversation.StopCommand.ExecuteAsync(null);
            await WaitUntil(() => !conversation.IsAnsweringQuestion && !session.IsRunning, "withdrawn after stop", 30_000);
            Assert.True(composer.IsVisible);
            Assert.Empty(session.PendingQuestions);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static ToolCallBlockViewModel? Tool(SessionViewModel session) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks)
            .OfType<ToolCallBlockViewModel>().LastOrDefault(t => t.ToolName == "ask_user");

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
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
