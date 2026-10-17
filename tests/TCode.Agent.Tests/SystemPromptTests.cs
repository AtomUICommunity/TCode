using TCode.Agent.Coding;
using TCode.Agent.Coding.Prompt;
using TCode.Agent.Coding.Sessions;
using TCode.Agent.Coding.Workspace;
using TCode.Ai;
using TCode.Ai.Fake;
using TCode.Ai.Messages;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class SystemPromptTests
{
    private static string NewRepo()
    {
        var root = Directory.CreateTempSubdirectory("tcode-prompt-").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature/x\n");
        return root;
    }

    [Fact]
    public void Sections_AreOrdered_AndEnvironmentIncludesGitBranchAndDate()
    {
        var repo = NewRepo();
        var sections = SystemPromptBuilder.Build(new WorkspaceRoots(repo), [], FakeWireApi.Models[0], new DateOnly(2026, 10, 16));

        Assert.Equal("identity", sections[0].Name);
        Assert.Equal("cwd", sections[^1].Name);
        var env = Assert.Single(sections, s => s.Name == "environment").Text;
        Assert.Contains("Git repository: yes (branch feature/x)", env);
        Assert.Contains("Today's date: 2026-10-16", env);
        Assert.Contains("Fake Sonnet (fake-sonnet)", env);
        var rendered = SystemPromptBuilder.Render(sections);
        Assert.StartsWith("You are TCode", rendered);
        Assert.Contains("<environment>\n", rendered);
    }

    [Fact]
    public void ContextFiles_StopAtRepoRoot_PreferOverride_AndRespectBudget()
    {
        var parent = Directory.CreateTempSubdirectory("tcode-prompt-parent-").FullName;
        File.WriteAllText(Path.Combine(parent, "AGENTS.md"), "outside the repo");
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "root rules");
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "ignored: AGENTS.md wins");
        var sub = Path.Combine(repo, "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "AGENTS.md"), "ignored: override wins");
        File.WriteAllText(Path.Combine(sub, "AGENTS.override.md"), new string('x', SystemPromptBuilder.ContextBudgetBytes));

        var files = SystemPromptBuilder.LoadContextFiles(sub).Where(f => f.Path.StartsWith(parent, StringComparison.Ordinal)).ToList();

        Assert.Equal([Path.Combine(repo, "AGENTS.md"), Path.Combine(sub, "AGENTS.override.md")], files.Select(f => f.Path));
        Assert.Equal("root rules", files[0].Content);
        Assert.EndsWith("[truncated]", files[1].Content);
    }

    [Fact]
    public void Diff_ReportsUpdatedAndRemovedSections()
    {
        PromptSection[] before = [new("identity", "a"), new("rules", "r1"), new("project_context", "p")];
        PromptSection[] after = [new("identity", "changed identity is ignored"), new("rules", "r2")];

        var patch = SystemPromptBuilder.Diff(before, after);

        Assert.Equal("Updated system prompt section \"rules\":\n\n<rules>\nr2\n</rules>\n\nRemoved system prompt section \"project_context\".", patch);
        Assert.Null(SystemPromptBuilder.Diff(after, after));
    }

    [Fact]
    public async Task Role_FiltersToolsAndAddsSection_FromTheNextBatch()
    {
        var repo = NewRepo();
        var apis = new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]);
        await using var session = AgentSession.Create(apis, FakeWireApi.Models[1], repo, Path.Combine(repo, ".sessions"));
        var agent = (Agent)session.Agent;
        session.Prompt("hello");
        await agent.WaitForIdleAsync();
        Assert.Contains(agent.Tools, t => t.Name == "write");

        // 切到 Plan：下一批输入前去掉写工具，并以补丁加入 role 节
        session.Role = TCode.Agent.Coding.Assets.SessionRole.Plan;
        session.Prompt("explain");
        await agent.WaitForIdleAsync();
        Assert.DoesNotContain(agent.Tools, t => t.Name is "write" or "edit");
        Assert.Contains(agent.Tools, t => t.Name == "read");
        var patch = Assert.Single(agent.Messages.OfType<SystemMessage>());
        Assert.Contains("You are in Plan mode", patch.Text);

        // 回到 Agent：工具恢复，模式节换成“可以动手了”的说明（不是直接删掉）
        session.Role = TCode.Agent.Coding.Assets.SessionRole.Agent;
        session.Prompt("do it");
        await agent.WaitForIdleAsync();
        Assert.Contains(agent.Tools, t => t.Name == "write");
        var resumed = agent.Messages.OfType<SystemMessage>().Last().Text;
        Assert.StartsWith("Updated system prompt section \"mode\"", resumed);
        Assert.Contains("You are in Agent mode again", resumed);

        // 新会话一开始就是 Agent：没有模式节；@path 的说明在 rules 里
        await using var fresh = AgentSession.Create(apis, FakeWireApi.Models[1], repo, Path.Combine(repo, ".sessions"));
        var freshAgent = (Agent)fresh.Agent;
        fresh.Prompt("hi");
        await freshAgent.WaitForIdleAsync();
        Assert.DoesNotContain("<mode>", freshAgent.SystemPrompt);
        Assert.Contains("@path refers to a file or directory in the workspace", freshAgent.SystemPrompt);
    }

    [Fact]
    public async Task Session_FreezesInitialPrompt_AppendsPatchOnChange_AndRestoresFromFile()
    {
        var repo = NewRepo();
        var apis = new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]);
        File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "version one");
        string file;
        string initialPrompt;
        await using (var session = AgentSession.Create(apis, FakeWireApi.Models[1], repo, Path.Combine(repo, ".sessions")))
        {
            var agent = (Agent)session.Agent;
            initialPrompt = agent.SystemPrompt;
            Assert.Contains("version one", initialPrompt);

            session.Prompt("hello");
            await agent.WaitForIdleAsync();
            Assert.DoesNotContain(agent.Messages, m => m is SystemMessage);

            File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "version two");
            session.Prompt("again");
            await agent.WaitForIdleAsync();

            var patch = Assert.Single(agent.Messages.OfType<SystemMessage>());
            Assert.StartsWith("Updated system prompt section \"project_context\"", patch.Text);
            Assert.Contains("version two", patch.Text);
            Assert.Equal(initialPrompt, agent.SystemPrompt);
            file = session.Store.FilePath;
        }

        var snapshot = SessionStore.Read(file);
        Assert.Contains(snapshot.InitialSystemPrompt!, s => s.Text.Contains("version one"));
        Assert.Contains(snapshot.SystemPrompt!, s => s.Text.Contains("version two"));

        // 恢复：开头的系统提示来自文件，而不是磁盘上已经变化的 AGENTS.md
        await using var restored = AgentSession.Open(apis, FakeWireApi.Models[1], file);
        Assert.Equal(initialPrompt, ((Agent)restored.Agent).SystemPrompt);
        restored.Prompt("third");
        await restored.Agent.WaitForIdleAsync();
        Assert.Single(restored.Agent.Messages.OfType<SystemMessage>());
    }
}
