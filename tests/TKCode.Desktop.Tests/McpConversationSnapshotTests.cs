using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 会话中的 MCP 端到端：真实 Host + stdio 测试服务器 + Fake 模型（“mcp:&lt;工具&gt; &lt;参数&gt;” 触发调用）。
/// Ask 档下 MCP 工具弹出审批（显示参数）；服务器的 elicitation 以表单卡片出现，提交后结果回到工具调用。
/// </summary>
public sealed class McpConversationSnapshotTests
{
    [Fact]
    public async Task McpTool_ApprovalAndElicitation_InConversation()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-mcp-chat-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-mcp-chat-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net10.0")));
        var serverDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../TKCode.Mcp.TestServer/bin/Debug/net10.0/TKCode.Mcp.TestServer.dll"));
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
        {
          "mcpServers": {
            "tester": { "command": "dotnet", "args": [{{JsonSerializer.Serialize(serverDll)}}], "alwaysAllow": ["ask"] }
          }
        }
        """);
        // 项目的 .mcp.json：未信任前不会启动，Composer 上方提示信任
        File.WriteAllText(Path.Combine(project, ".mcp.json"), $$"""
        { "mcpServers": { "proj": { "command": "dotnet", "args": [{{JsonSerializer.Serialize(serverDll)}}] } } }
        """);
        var state = new TKCode.Desktop.Services.StateService(Path.Combine(home, "state.json"));
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
            await conversation.Mcp.RefreshAttentionAsync();
            await WaitUntil(() => conversation.Mcp.HasAttention, "untrusted project server hint");
            var attention = Assert.Single(conversation.Mcp.Attention);
            Assert.True(attention.Untrusted);
            Assert.Contains("proj", attention.Text);
            HeadlessApp.Save(window, "mcp-untrusted");
            await conversation.Mcp.TrustCommand.ExecuteAsync(attention);
            await WaitUntil(() => !conversation.Mcp.HasAttention, "hint cleared after trust");

            // 1. 需要审批的 MCP 工具：卡片显示服务器 · 工具与参数
            conversation.ComposerText = """mcp:mcp__tester__echo {"text":"hello from tkcode"}""";
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => conversation.Active?.CurrentApproval is not null, "approval requested", 40_000);
            var session = conversation.Active!;
            var approval = session.CurrentApproval!;
            Assert.Equal("mcp", approval.Request.Kind);
            Assert.Contains("tester · Echo", approval.Title);
            Assert.Contains("hello from tkcode", approval.Details);
            await Settle();
            HeadlessApp.Save(window, "mcp-approval");
            approval.AllowOnceCommand.Execute(null);
            await WaitUntil(() => !session.IsRunning && Tool(session, "mcp__tester__echo")?.Result is { Length: > 0 }, "echo finished");
            Assert.Contains("echo: hello from tkcode", Tool(session, "mcp__tester__echo")!.Result);
            Assert.Equal("tester · echo", Tool(session, "mcp__tester__echo")!.Verb);

            // 2. elicitation：ask 已在 alwaysAllow 中，直接执行；服务器请求填写名字
            conversation.ComposerText = "mcp:mcp__tester__ask {}";
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => session.CurrentElicitation is not null, "elicitation shown", 30_000);
            var elicitation = session.CurrentElicitation!;
            Assert.True(elicitation.IsForm);
            var name = Assert.Single(elicitation.Fields);
            Assert.True(name.Required);
            elicitation.SubmitCommand.Execute(null);
            Assert.True(name.HasError);
            name.TextValue = "Ada";
            await Settle();
            HeadlessApp.Save(window, "mcp-elicitation");
            elicitation.SubmitCommand.Execute(null);
            await WaitUntil(() => !session.IsRunning && Tool(session, "mcp__tester__ask")?.Result is { Length: > 0 }, "ask finished", 30_000);
            Assert.Contains("hello Ada", Tool(session, "mcp__tester__ask")!.Result);
            Assert.False(session.HasPendingElicitation);
            await Settle();
            HeadlessApp.Save(window, "mcp-conversation");

            // 3. @ 资源：输入 @read 出现补全列表，确认后成为资源 tag；发送时 Host 读取内容附在消息中
            foreach (var popup in window.GetVisualDescendants().OfType<TKCode.Desktop.Motion.Popover>().Where(p => p.Name == "CompletionPopup"))
            {
                popup.ShouldUseOverlayLayer = true;
            }
            window.FindControl<ComposerView>("DockedComposer")!.FindControl<TKCode.Desktop.Controls.Composer.ComposerEditor>("Input")!.Focus();
            conversation.ComposerText = "看看 @read";
            await WaitUntil(() => conversation.Completion.Items.Count > 0, "resource picker");
            Assert.Equal("Project readme", conversation.Completion.Items[0].Title);
            await Settle();
            HeadlessApp.Save(window, "mcp-resource-picker");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.Equal("看看 @tester:test://readme ", conversation.ComposerText);
            Assert.Single(McpComposerViewModel.ResourcesIn(conversation.Composer.Segments));
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<UserMessageItemViewModel>().Count() == 3, "resource turn finished");
            var withResource = session.Items.OfType<UserMessageItemViewModel>().Last();
            Assert.Contains("Hello resource.", withResource.Text);
            Assert.Equal("看看 @tester:test://readme", withResource.DisplayText);

            // 4. / 提示词：选择后展开参数，必填参数为空时不发送
            conversation.ComposerText = "/rev";
            await WaitUntil(() => conversation.Completion.Items.Count > 0, "prompt picker");
            Assert.True(conversation.Completion.AcceptHighlighted());
            Assert.True(conversation.Mcp.HasPrompt);
            Assert.Equal("", conversation.ComposerText);
            conversation.SendCommand.Execute(null);
            await Settle();
            Assert.NotNull(conversation.StatusMessage);
            conversation.Mcp.PromptArguments.Single().Value = "a.cs";
            await Settle();
            HeadlessApp.Save(window, "mcp-prompt");
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<UserMessageItemViewModel>().Count() == 4, "prompt turn finished");
            Assert.Contains("Please review a.cs.", session.Items.OfType<UserMessageItemViewModel>().Last().Text);
            Assert.False(conversation.Mcp.HasPrompt);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<TKCode.Desktop.Services.AgentHostConnection>().Dispose();
    }

    private static ToolCallBlockViewModel? Tool(SessionViewModel session, string name) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks)
            .Concat(session.Items.OfType<ActivityGroupViewModel>().SelectMany(g => g.Steps))
            .OfType<ToolCallBlockViewModel>().LastOrDefault(t => t.ToolName == name);

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
