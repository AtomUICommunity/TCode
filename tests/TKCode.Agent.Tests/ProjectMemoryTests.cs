using System.Text.Json;
using System.Text.RegularExpressions;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Memory;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>项目记忆（docs/design/memory.md）：目录定位、索引、写入后处理、审批、注入与压缩。</summary>
public sealed class ProjectMemoryTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tkcode-memory-").FullName;

    private string Memory => Path.Combine(_root, "memory");

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<AgentToolResult> Run(IAgentTool tool, Dictionary<string, string> args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args, PlanTestJson.Default.DictionaryStringString));
        return tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
    }

    private static string Text(AgentToolResult result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    private static string Lines(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"- [M{i}](m{i}.md) — hook {i}")) + "\n";

    [Fact]
    public void ProjectRoot_IsTheMainRepository_ForWorktrees_AndTheSubmoduleItself()
    {
        var main = Path.Combine(_root, "main");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt"));
        File.WriteAllText(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(main, ".git", "worktrees", "wt", "commondir"), "../..\n");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt")}\n");

        Directory.CreateDirectory(Path.Combine(main, ".git", "modules", "sub"));
        var submodule = Path.Combine(main, "sub");
        Directory.CreateDirectory(submodule);
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/sub\n");

        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Equal(main, ProjectMemory.ProjectRoot(Path.Combine(main)));
        Assert.Equal(main, ProjectMemory.ProjectRoot(Path.Combine(worktree, "src")));
        Assert.Equal(submodule, ProjectMemory.ProjectRoot(submodule));
        Assert.Equal(plain, ProjectMemory.ProjectRoot(plain));
        Assert.Equal(ProjectMemory.DirectoryFor(main), ProjectMemory.DirectoryFor(worktree));
        Assert.StartsWith(Path.Combine(TKCodePaths.Home, "projects"), ProjectMemory.DirectoryFor(main), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectKey_IsASanitizedSlugPlusAStableHash()
    {
        var spaced = Path.Combine(_root, "My Repo!");
        var dashed = Path.Combine(_root, "My-Repo-");
        var key = ProjectMemory.ProjectKey(spaced);

        Assert.Matches(new Regex("^[A-Za-z0-9_][A-Za-z0-9._-]*-My-Repo-[0-9a-f]{8}$"), key);
        Assert.Equal(key, ProjectMemory.ProjectKey(spaced + Path.DirectorySeparatorChar));
        Assert.NotEqual(key, ProjectMemory.ProjectKey(dashed));

        var deep = ProjectMemory.ProjectKey(Path.Combine(_root, new string('a', 100), new string('b', 100)));
        Assert.Equal(64 + 1 + 8, deep.Length);
        Assert.StartsWith("b", deep, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexText_KeepsTheFirst200LinesOr25KB_AndSaysWhatWasLeftOut()
    {
        var index = Path.Combine(Memory, "MEMORY.md");

        Assert.Equal($"Your project memory index ({index.Replace('\\', '/')}) is empty: no memories have been saved for this project yet.",
            ProjectMemory.IndexText(index, " \n"));

        var small = ProjectMemory.IndexText(index, "- [A](a.md) — hook\r\n");
        Assert.Equal($"Contents of {index.Replace('\\', '/')} (your project memory index):\n\n- [A](a.md) — hook", small);

        var long250 = ProjectMemory.IndexText(index, Lines(250));
        Assert.Contains("hook 200\n\n[MEMORY.md was cut off here: 50 more lines were not loaded.", long250);
        Assert.DoesNotContain("hook 201", long250);

        var wide = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"{i}:" + new string('x', 400)));
        var cut = ProjectMemory.IndexText(index, wide);
        Assert.Contains("\n\n[MEMORY.md was cut off here: 37 more lines were not loaded.", cut);
    }

    [Fact]
    public void Stamp_SetsModifiedAndKeepsTheOriginSession_OnlyInFrontmatter()
    {
        var now = new DateTimeOffset(2026, 10, 19, 8, 30, 15, TimeSpan.FromHours(8));
        const string fresh = "---\nname: a\ndescription: d\ntype: user\n---\n\nBody\n";

        var stamped = ProjectMemory.Stamp(fresh, "s1", now);
        Assert.Equal("---\nname: a\ndescription: d\ntype: user\nmodified: 2026-10-19T00:30:15Z\noriginSessionId: s1\n---\n\nBody\n", stamped);

        var again = ProjectMemory.Stamp(stamped, "s2", now.AddDays(1));
        Assert.Equal("---\nname: a\ndescription: d\ntype: user\nmodified: 2026-10-20T00:30:15Z\noriginSessionId: s1\n---\n\nBody\n", again);

        Assert.Equal("No frontmatter\n", ProjectMemory.Stamp("No frontmatter\n", "s1", now));
        Assert.Equal("---\nunclosed\n", ProjectMemory.Stamp("---\nunclosed\n", "s1", now));
        // 嵌套映射里的同名键不算顶层键
        Assert.Contains("\n  modified: old\nmodified: 2026-10-19T00:30:15Z\n",
            ProjectMemory.Stamp("---\nmetadata:\n  modified: old\n---\n", "s1", now));
    }

    [Fact]
    public void IndexReminder_WarnsNearTheLimit_AndAsksToShortenOverIt()
    {
        Assert.Null(ProjectMemory.IndexReminder(Lines(10)));
        Assert.Contains("MEMORY.md is 160 lines", ProjectMemory.IndexReminder(Lines(160)));
        Assert.Contains("MEMORY.md is now 210 lines", ProjectMemory.IndexReminder(Lines(210)));
        Assert.Contains("over the limit", ProjectMemory.IndexReminder(new string('x', 26 * 1024)));
    }

    [Fact]
    public void Permissions_MemoryReadsAndMarkdownWrites_NeedNoApproval()
    {
        var workspaceDir = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var outside = WriteFile("outside/secret.md", "secret");
        Directory.CreateDirectory(Memory);
        File.CreateSymbolicLink(Path.Combine(Memory, "link.md"), outside);
        var workspace = new WorkspaceRoots(workspaceDir) { MemoryDirectory = Memory };
        var approvals = new SessionApprovals();
        PermissionVerdict Evaluate(ToolPermission permission, WorkspaceRoots? roots = null) =>
            PermissionPolicy.Evaluate(permission, PermissionMode.Ask, roots ?? workspace, approvals);

        Assert.True(Evaluate(new(ToolAccess.Read, [Path.Combine(Memory, "MEMORY.md")])).Allowed);
        Assert.True(Evaluate(new(ToolAccess.Read, [Memory])).Allowed);
        Assert.True(Evaluate(new(ToolAccess.Write, [Path.Combine(Memory, "pref.md")])).Allowed);
        Assert.True(Evaluate(new(ToolAccess.Execute, Command: $"ls {Memory}")).Allowed);
        Assert.True(Evaluate(new(ToolAccess.Execute, Command: $"cat {Path.Combine(Memory, "MEMORY.md")}")).Allowed);

        Assert.False(Evaluate(new(ToolAccess.Write, [Path.Combine(Memory, "notes.txt")])).Allowed);
        Assert.False(Evaluate(new(ToolAccess.Write, [Path.Combine(Memory, "..", "escape.md")])).Allowed);
        Assert.False(Evaluate(new(ToolAccess.Write, [Path.Combine(Memory, "link.md")])).Allowed);
        Assert.False(Evaluate(new(ToolAccess.Read, [Path.Combine(Memory, "link.md")])).Allowed);
        // 工作区内的普通写入仍按原规则询问
        Assert.False(Evaluate(new(ToolAccess.Write, [Path.Combine(workspaceDir, "a.md")])).Allowed);
        // 未启用记忆时记忆目录就是工作区外
        Assert.False(Evaluate(new(ToolAccess.Read, [Path.Combine(Memory, "MEMORY.md")]), new WorkspaceRoots(workspaceDir)).Allowed);
    }

    [Fact]
    public async Task WriteAndEdit_StampMemoryFiles_AndRemindAboutALongIndex()
    {
        var workspaceDir = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var context = new ToolContext(new WorkspaceRoots(workspaceDir) { MemoryDirectory = Memory }, "session-1");
        var memoryFile = Path.Combine(Memory, "pref.md");

        var created = await Run(new WriteTool(context), new() { ["path"] = memoryFile, ["content"] = "---\nname: pref\ndescription: d\ntype: user\n---\n\nUses pnpm.\n" });
        Assert.False(created.IsError, Text(created));
        var text = File.ReadAllText(memoryFile);
        Assert.Contains("\noriginSessionId: session-1\n---\n", text);
        Assert.Matches(new Regex(@"\nmodified: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\n"), text);

        var edited = await Run(new EditTool(context), new() { ["path"] = memoryFile, ["oldText"] = "Uses pnpm.", ["newText"] = "Uses pnpm, never npm." });
        Assert.False(edited.IsError, Text(edited));
        var after = File.ReadAllText(memoryFile);
        Assert.Contains("Uses pnpm, never npm.", after);
        Assert.Single(Regex.Matches(after, "^modified: ", RegexOptions.Multiline));

        var index = await Run(new WriteTool(context), new() { ["path"] = Path.Combine(Memory, "MEMORY.md"), ["content"] = Lines(210) });
        Assert.False(index.IsError);
        Assert.Contains("over the limit of 200 lines / 25 KB", Text(index));
        Assert.DoesNotContain("modified:", File.ReadAllText(Path.Combine(Memory, "MEMORY.md")));

        var normal = Path.Combine(workspaceDir, "doc.md");
        await Run(new WriteTool(context), new() { ["path"] = normal, ["content"] = "---\nname: x\n---\n" });
        Assert.Equal("---\nname: x\n---\n", File.ReadAllText(normal));
    }

    [Fact]
    public async Task PlanMode_CanWriteMemoryFiles_ButNotOtherFiles()
    {
        var workspaceDir = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var context = new ToolContext(new WorkspaceRoots(workspaceDir) { MemoryDirectory = Memory }, "session-1")
        {
            PlanFile = Path.Combine(_root, "plan.md"),
            Role = SessionRole.Plan,
        };
        Assert.False((await Run(new WriteTool(context), new() { ["path"] = Path.Combine(Memory, "a.md"), ["content"] = "x" })).IsError);
        Assert.True((await Run(new WriteTool(context), new() { ["path"] = Path.Combine(workspaceDir, "a.md"), ["content"] = "x" })).IsError);
    }

    private (AgentSession Session, ScriptedWireApi Api) CreateSession(bool memory, params ScriptedWireApi.Turn[] turns)
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var api = new ScriptedWireApi(turns);
        var session = AgentSession.Create(new WireApiRegistry([api]), ScriptedWireApi.Model, repo, Path.Combine(_root, ".sessions"),
            options: new BuiltinOptions { MemoryDirectory = memory ? Memory : null });
        return (session, api);
    }

    private static async Task RunAsync(AgentSession session, string prompt)
    {
        await session.Agent.PromptAsync(UserMessage.FromText(prompt));
        await session.Agent.WaitForIdleAsync();
    }

    private static SystemMessage[] IndexMessages(IEnumerable<Message> messages) => messages.Where(ProjectMemory.IsIndexMessage).Cast<SystemMessage>().ToArray();

    [Fact]
    public async Task Session_AddsTheMemorySection_AndInjectsTheIndexOnce_AlsoAfterReopening()
    {
        WriteFile("memory/MEMORY.md", "- [Pref](pref.md) — uses pnpm\n");
        var (session, api) = CreateSession(memory: true);
        await RunAsync(session, "hi");
        await RunAsync(session, "again");

        Assert.Contains("<memory>\nYou have a persistent, file-based memory for this project at " + Memory.Replace('\\', '/'), api.Contexts[0].SystemPrompt);
        var first = Assert.IsType<SystemMessage>(api.Contexts[0].Messages[0]);
        Assert.Equal(ProjectMemory.Source, first.Source);
        Assert.Contains("- [Pref](pref.md) — uses pnpm", first.Text);
        Assert.Single(IndexMessages(api.Contexts[1].Messages));
        Assert.Equal(Memory, session.Workspace.MemoryDirectory);

        var file = session.Store.FilePath;
        await session.DisposeAsync();
        var reopenedApi = new ScriptedWireApi();
        await using var reopened = AgentSession.Open(new WireApiRegistry([reopenedApi]), ScriptedWireApi.Model, file,
            options: new BuiltinOptions { MemoryDirectory = Memory });
        await RunAsync(reopened, "after reopening");
        Assert.Single(IndexMessages(reopenedApi.Contexts[0].Messages));
    }

    [Fact]
    public async Task Session_WithoutMemory_HasNoSectionAndNoIndex()
    {
        WriteFile("memory/MEMORY.md", "- [Pref](pref.md) — uses pnpm\n");
        var (session, api) = CreateSession(memory: false);
        await using var _ = session;
        await RunAsync(session, "hi");

        Assert.DoesNotContain("<memory>", api.Contexts[0].SystemPrompt);
        Assert.Empty(IndexMessages(api.Contexts[0].Messages));
        Assert.Null(session.Workspace.MemoryDirectory);
    }

    [Fact]
    public async Task Compaction_StartsTheNewHistoryWithAFreshIndex()
    {
        WriteFile("memory/MEMORY.md", "- [Old](old.md) — before\n");
        var (session, api) = CreateSession(memory: true,
            ScriptedWireApi.Turn.Reply("ok"), ScriptedWireApi.Turn.Reply("<summary>We said hi.</summary>"));
        await using var _ = session;
        await RunAsync(session, "hi");

        WriteFile("memory/MEMORY.md", "- [New](new.md) — after\n");
        await session.CompactAsync(null);
        var index = Assert.IsType<SystemMessage>(session.Agent.Messages[0]);
        Assert.Equal(ProjectMemory.Source, index.Source);
        Assert.Contains("- [New](new.md) — after", index.Text);

        await RunAsync(session, "next");
        var last = api.Contexts[^1].Messages;
        Assert.Contains("- [New](new.md) — after", Assert.Single(IndexMessages(last)).Text);
        Assert.True(ProjectMemory.IsIndexMessage(last[0]));
    }
}
