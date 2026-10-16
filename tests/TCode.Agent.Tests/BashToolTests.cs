using System.Text.Json;
using TCode.Agent.Coding;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Shell;
using TCode.Agent.Coding.Workspace;
using TCode.Ai;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class BashToolTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tcode-bash-").FullName;
    private readonly ToolContext _context;

    public BashToolTests() => _context = new ToolContext(new WorkspaceRoots(_root), "bash-" + Guid.NewGuid().ToString("N")[..8]);

    private Task<AgentToolResult> Run(string json, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(json);
        return new BashTool(_context).ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), ct);
    }

    private static string Text(AgentToolResult result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    private static string Cmd(string unix, string windows) => OperatingSystem.IsWindows() && ShellResolver.Current.IsPowerShell ? windows : unix;

    [Fact]
    public async Task RunsInWorkspaceRoot_AndReportsExitCode()
    {
        var ok = await Run(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = Cmd("pwd && echo hello 1>&2", "Get-Location; Write-Error hello") },
            TestJsonContext.Default.DictionaryStringString));
        Assert.False(ok.IsError, Text(ok));
        Assert.Contains("hello", Text(ok));
        Assert.Contains(Path.GetFileName(_root), Text(ok));

        var failed = await Run("""{"command":"exit 3"}""");
        Assert.True(failed.IsError);
        Assert.Contains("exited with code 3", Text(failed));
        Assert.Equal(3, Assert.IsType<CommandDetails>(ToolDetails.FromJson(failed.Details)).ExitCode);
    }

    [Fact]
    public async Task Timeout_KillsTheProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var started = DateTime.UtcNow;
        var result = await Run("""{"command":"sleep 30","timeout":1000}""");
        Assert.True(result.IsError);
        Assert.Contains("timed out", Text(result));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task LongOutput_IsTruncatedToTail_WithFullLogFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var result = await Run("""{"command":"for i in $(seq 1 5000); do echo line $i; done"}""");
        var text = Text(result);
        Assert.Contains("line 5000", text);
        Assert.DoesNotContain("line 1\n", text);
        var details = Assert.IsType<CommandDetails>(ToolDetails.FromJson(result.Details));
        Assert.NotNull(details.OutputPath);
        Assert.Contains("line 1\n", File.ReadAllText(details.OutputPath!));
    }

    [Fact]
    public async Task Abort_KillsProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("""{"command":"sleep 30 & sleep 30; wait"}""", cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Background_ReturnsImmediately_AndCompletes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var completed = new TaskCompletionSource<BackgroundTask>();
        _context.BackgroundTasks.Completed += (task, _) => completed.TrySetResult(task);
        var result = await Run("""{"command":"echo started; sleep 0.3; echo done","run_in_background":true,"description":"short job"}""");
        Assert.Contains("Started background task b1", Text(result));

        var task = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", task.Status);
        Assert.Contains("done", File.ReadAllText(task.OutputPath!));
        Assert.Contains("<task-id>b1</task-id>", BackgroundTasks.FormatNotification(task));
    }

    [Fact]
    public async Task TaskStop_StopsBackgroundCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var completed = new TaskCompletionSource<BackgroundTask>();
        _context.BackgroundTasks.Completed += (task, _) => completed.TrySetResult(task);
        await Run("""{"command":"sleep 30","run_in_background":true}""");
        using var doc = JsonDocument.Parse("""{"task_id":"b1"}""");
        var stop = await new TaskStopTool(_context).ExecuteAsync(new ToolInvocation("c", doc.RootElement.Clone()), CancellationToken.None);
        Assert.Contains("Stopped task b1", Text(stop));
        Assert.Equal("stopped", (await completed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task Session_InjectsTaskNotification_AndStartsNewTurn()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var api = new ScriptedWireApi(
            ScriptedWireApi.Turn.Tools(("bash", """{"command":"sleep 0.2; echo finished","run_in_background":true}""")),
            ScriptedWireApi.Turn.Reply("started it"),
            ScriptedWireApi.Turn.Reply("it finished"));
        await using var session = AgentSession.Create(new WireApiRegistry([api]), ScriptedWireApi.Model, _root, Path.Combine(_root, ".sessions"),
            options: new BuiltinOptions());
        session.Agent.PermissionMode = PermissionMode.FullAccess;
        session.Prompt("run something in the background");
        await session.Agent.WaitForIdleAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!session.Agent.Messages.OfType<UserMessage>().Any(m => m.Source == "task-notification") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await session.Agent.WaitForIdleAsync();
        var notification = Assert.Single(session.Agent.Messages.OfType<UserMessage>(), m => m.Source == "task-notification");
        Assert.Contains("<status>completed</status>", ((TextContent)notification.Content[0]).Text);
        Assert.Equal("it finished", ((TextContent)((AssistantMessage)session.Agent.Messages[^1]).Content[0]).Text);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
