using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 一轮里的文件改动：工具结果的 diff 在轮次结束后汇总成卡片，撤销经宿主发出，变更面板列出整个会话的改动。
/// </summary>
public sealed class ChangeFlowTests
{
    private const string EditDetails =
        """{"kind":"diff","path":"/p/notes.txt","diff":"--- a/notes.txt\n+++ b/notes.txt\n@@ -1,1 +1,1 @@\n-one\n+two\n","added":1,"removed":1,"firstChangedLine":1,"before":"one\n","afterSha256":"abc123"}""";

    [Fact]
    public async Task TurnEnd_AddsChangeCard_AndRevertGoesThroughHost()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-change-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-change-project-").FullName;
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
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            RecordedRevert? sent = null;
            session.RevertFile = request =>
            {
                sent = new RecordedRevert(request.Path, request.ExpectedSha256, request.RestoreText);
                return Task.FromResult(new RevertFileResult(null));
            };

            session.Apply(new AgentStarted());
            session.Apply(new MessageStarted("u2", "user", "把 one 改成 two"));
            session.Apply(new MessageStarted("m1", "assistant", null));
            session.Apply(new ToolCallStarted("m1", 0, "e1", "edit"));
            session.Apply(new ToolCallEnded("m1", 0, "e1", """{"path":"/p/notes.txt"}"""));
            session.Apply(new ToolExecutionStarted("e1", "edit", """{"path":"/p/notes.txt"}"""));
            session.Apply(new ToolExecutionEnded("e1", "edit", false, "Replaced 1 block(s) in /p/notes.txt.", 12, EditDetails));
            session.Apply(new MessageEnded("m1", "assistant", "Stop", null, null));
            session.Apply(new AgentEnded());

            var card = Assert.Single(session.Items.OfType<TurnChangesItemViewModel>());
            var file = Assert.Single(card.Files);
            Assert.Equal("/p/notes.txt", file.Path);
            Assert.Equal("+1 −1", file.Stats);
            Assert.True(session.HasChanges);
            Assert.Equal("+1 −1", session.ChangesStats);

            await card.RevertAllCommand.ExecuteAsync(null);

            Assert.Equal(new RecordedRevert("/p/notes.txt", "abc123", "one\n"), sent);
            Assert.True(file.IsReverted);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed record RecordedRevert(string Path, string Sha, string? Restore);

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
    }
}
