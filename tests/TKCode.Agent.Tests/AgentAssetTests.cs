using TKCode.Agent.Coding.Assets;
using TKCode.Config;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>command / skill / role 的发现与合并（docs/design/composer.md §4、§5）以及 frontmatter 解析。</summary>
public sealed class AgentAssetTests
{
    [Fact]
    public void Frontmatter_ParsesTheSubsetWeUse()
    {
        var fm = Frontmatter.Parse("""
            ---
            name: "Reviewer" # 显示名
            description: >
              Read-only code review
              with file:line references
            tools: [read, grep, "web_fetch"]
            aliases:
              - rv
              - 'code-review'
            model: anthropic/claude-opus-5-5
            metadata:
              owner: me
            instructions: |
              line 1
                indented
            ---
            # Body

            You are reviewing.
            """);

        Assert.Empty(fm.Warnings);
        Assert.Equal("Reviewer", fm.GetString("name"));
        Assert.Equal("Read-only code review with file:line references", fm.GetString("description"));
        Assert.Equal(["read", "grep", "web_fetch"], fm.GetList("tools"));
        Assert.Equal(["rv", "code-review"], fm.GetList("aliases"));
        Assert.Equal("anthropic/claude-opus-5-5", fm.GetString("MODEL"));
        Assert.True(fm.Fields.ContainsKey("metadata"));
        Assert.Equal("line 1\n  indented", fm.GetString("instructions"));
        Assert.StartsWith("# Body", fm.Body);

        var plain = Frontmatter.Parse("Just a prompt.\n---\nnot frontmatter");
        Assert.Empty(plain.Fields);
        Assert.Equal("Just a prompt.\n---\nnot frontmatter", plain.Body);
        Assert.NotEmpty(Frontmatter.Parse("---\nname: x\n").Warnings);
        Assert.Equal("a,b", Frontmatter.Parse("---\nlist: a,b\n---\n").GetString("list"));
    }

    [Fact]
    public void Load_MergesGlobalAndProject_WithOverridesAndProblems()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-assets-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-assets-project-").FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Write(home, "commands/review.md", "Review the diff globally.");
        Write(home, "commands/git/commit.md", "---\ndescription: Commit staged changes\n---\nWrite a commit message.");
        Write(home, "commands/pdf.md", "Command shadowed by the pdf skill.");
        Write(home, "commands/bad name.md", "x");
        Write(home, "skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Read PDFs\n---\n# PDF");
        Write(home, "skills/nodesc/SKILL.md", "# no frontmatter");
        Write(home, "skills/empty-dir/readme.txt", "not a skill");
        Write(home, "roles/reviewer.md", "---\nname: Reviewer\ntools: readonly\nicon: eye\n---\nReview only.");
        Write(project, ".agents/commands/review.md", "---\ndescription: Project review\n---\nReview with project rules.");
        Write(project, ".agents/skills/deploy/SKILL.md", "---\nname: ship\ndescription: Deploy\n---\nSteps");
        Write(project, ".agents/roles/writer.md", "Write docs.");

        var catalog = AgentAssetLoader.Load(home, AgentAssetLoader.ProjectRoot(Path.Combine(project)));

        Assert.Equal(["git:commit", "review"], catalog.Commands.Select(c => c.Name));
        var review = catalog.FindCommand("REVIEW")!;
        Assert.Equal((AssetSource.Project, "Project review"), (review.Source, review.Description));
        Assert.Equal("Commit staged changes", catalog.FindCommand("git:commit")!.Description);
        Assert.Equal(["deploy", "nodesc", "pdf"], catalog.Skills.Select(s => s.Name));
        Assert.Null(catalog.FindSkill("nodesc")!.Description);
        var reviewer = catalog.FindRole("reviewer")!;
        Assert.Equal(("Reviewer", "eye"), (reviewer.DisplayName, reviewer.Icon));
        Assert.Equal(["readonly"], reviewer.Tools);
        Assert.Equal(["all"], catalog.FindRole("writer")!.Tools);

        // 覆盖与遮住
        Assert.Contains(catalog.Shadowed, s => s.Hidden.Name == "review" && s.Hidden.Source == AssetSource.Global && s.By.Source == AssetSource.Project);
        Assert.Contains(catalog.Shadowed, s => s.Hidden is { Kind: AssetKind.Command, Name: "pdf" } && s.By.Kind == AssetKind.Skill);
        // 问题：名字不合法、skill 名与目录不一致、skill 缺少描述
        Assert.Contains(catalog.Problems, p => p.Path.EndsWith("bad name.md") && p.Message.Contains("Invalid command name"));
        Assert.Contains(catalog.Problems, p => p.Message.Contains("'ship' does not match its directory 'deploy'"));
        Assert.Contains(catalog.Problems, p => p.Path.Contains("nodesc") && p.Message.Contains("no description"));

        Assert.Equal("Steps", AgentAssetStore.ReadBody(catalog.FindSkill("deploy")!.Path));
    }

    [Fact]
    public void Load_SkipsOversizedFiles()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-assets-home-").FullName;
        Write(home, "commands/huge.md", new string('x', (int)AgentAssetLoader.MaxFileBytes + 1));
        var catalog = AgentAssetLoader.Load(home, null);
        Assert.Empty(catalog.Commands);
        Assert.Contains(catalog.Problems, p => p.Message.Contains("larger than"));
    }

    [Fact]
    public async Task Store_ReloadsAndNotifies_WhenProjectAgentsDirectoryAppearsAndChanges()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-assets-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-assets-project-").FullName;
        using var store = new AgentAssetStore(home);
        var changes = new List<string?>();
        store.Changed += root =>
        {
            lock (changes)
            {
                changes.Add(root);
            }
        };
        Assert.Empty(store.Get(project).Commands);

        // .agents 在加载之后才创建
        Write(project, ".agents/commands/hello.md", "Say hello.");
        await WaitUntil(() => store.Get(project).Commands.Count == 1, "project command after .agents is created");

        // 之后的修改也能看到
        Write(project, ".agents/commands/bye.md", "Say bye.");
        await WaitUntil(() => store.Get(project).Commands.Count == 2, "second project command");

        // 全局目录的变化影响所有项目
        Write(home, "skills/pdf/SKILL.md", "---\ndescription: Read PDFs\n---\n");
        await WaitUntil(() => store.Get(project).Skills.Count == 1, "global skill");
        lock (changes)
        {
            Assert.Contains(null, changes);
            Assert.Contains(changes, c => c is not null);
        }

        // 数据目录里其他文件（会话记录）的变化不触发
        lock (changes)
        {
            changes.Clear();
        }
        Write(home, "sessions/x.jsonl", "{}");
        await Task.Delay(800);
        lock (changes)
        {
            Assert.Empty(changes);
        }
    }

    [Fact]
    public async Task SkillSource_ListsDiscoverableSkills_AndToolLoadsThem()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-assets-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-assets-project-").FullName;
        using var store = new AgentAssetStore(home);
        var context = new TKCode.Agent.Coding.Tools.ToolContext(new TKCode.Agent.Coding.Workspace.WorkspaceRoots(project), "skills-test");

        // 没有 skill：不提供工具，也不加系统提示节
        var enabled = true;
        await using var source = new SkillToolSource(store, context, () => enabled);
        Assert.Empty((await source.RefreshAsync(true, CancellationToken.None)).Tools);

        Write(home, "skills/pdf/SKILL.md", "---\nname: pdf\ndescription: |\n  Read PDF files.\n  Use for forms.\n---\n# PDF\nRun scripts/extract.py");
        Write(project, ".agents/skills/hidden/SKILL.md", "no description, explicit use only");
        // 目录变化经防抖后生效
        await WaitUntil(() => store.Get(project).Skills.Count == 2, "skills reloaded");
        var set = await source.RefreshAsync(false, CancellationToken.None);
        var section = Assert.Single(set.Sections);
        Assert.Equal("skills", section.Name);
        Assert.Contains("- pdf: Read PDF files. Use for forms.", section.Text);
        Assert.DoesNotContain("hidden", section.Text);

        var tool = Assert.Single(set.Tools);
        Assert.Equal(TKCode.Agent.ToolAccess.None, tool.DescribePermission(Json("""{"name":"pdf"}""")).Access);
        var result = await tool.ExecuteAsync(new TKCode.Agent.ToolInvocation("1", Json("""{"name":"/PDF"}""")), CancellationToken.None);
        var text = Assert.IsType<TKCode.Ai.Messages.TextContent>(Assert.Single(result.Content)).Text;
        Assert.StartsWith($"<skill name=\"pdf\" directory=\"{Path.Combine(home, "skills", "pdf")}\">", text);
        Assert.Contains("Run scripts/extract.py", text);
        Assert.DoesNotContain("description:", text);
        // 没有描述的 skill 也能显式加载
        Assert.False((await tool.ExecuteAsync(new TKCode.Agent.ToolInvocation("2", Json("""{"name":"hidden"}""")), CancellationToken.None)).IsError);
        var unknown = await tool.ExecuteAsync(new TKCode.Agent.ToolInvocation("3", Json("""{"name":"nope"}""")), CancellationToken.None);
        Assert.True(unknown.IsError);

        // settings 关闭自动发现
        enabled = false;
        Assert.Empty((await source.RefreshAsync(false, CancellationToken.None)).Sections);
    }

    [Fact]
    public async Task CompositeSource_MergesInOrder_AndSurvivesAFailingSource()
    {
        var a = new FixedSource(new DynamicToolSetBuilder("a").Build());
        var b = new FixedSource(null);
        var c = new FixedSource(new DynamicToolSetBuilder("c").Build());
        await using var composite = new CompositeToolSource([a, b, c]);
        var set = await composite.RefreshAsync(false, CancellationToken.None);
        Assert.Equal(["a", "c"], set.Sections.Select(s => s.Name));
    }

    private sealed class DynamicToolSetBuilder(string name)
    {
        public TKCode.Agent.Coding.Tools.DynamicToolSet Build() => new([], [new TKCode.Agent.Coding.Prompt.PromptSection(name, name)]);
    }

    private sealed class FixedSource(TKCode.Agent.Coding.Tools.DynamicToolSet? set) : TKCode.Agent.Coding.Tools.IDynamicToolSource
    {
        public ValueTask<TKCode.Agent.Coding.Tools.DynamicToolSet> RefreshAsync(bool firstTurn, CancellationToken cancellationToken) =>
            set is null ? throw new InvalidOperationException("boom") : ValueTask.FromResult(set);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Role_ResolvesBuiltinAndCustom_AndFiltersTools()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-assets-home-").FullName;
        Write(home, "roles/reviewer.md", "---\nname: Reviewer\ntools: [read, grep, mcp__gh__*]\nmodel: anthropic/claude-opus-5-5\n---\nReview only.");
        var catalog = AgentAssetLoader.Load(home, null);

        Assert.Same(SessionRole.Agent, SessionRole.Resolve(null, catalog));
        Assert.Same(SessionRole.Plan, SessionRole.Resolve("PLAN", catalog));
        Assert.Null(SessionRole.Resolve("missing", catalog));
        var reviewer = SessionRole.Resolve("reviewer", catalog)!;
        Assert.Equal(("Review only.", "anthropic/claude-opus-5-5"), (reviewer.Instructions, reviewer.Model));
        Assert.Equal("mode", reviewer.Section!.Name);
        Assert.Null(SessionRole.Agent.Section);

        TKCode.Agent.IAgentTool Tool(string name) => new NamedTool(name);
        Assert.True(reviewer.Allows(Tool("grep")));
        Assert.True(reviewer.Allows(Tool("mcp__gh__search")));
        Assert.False(reviewer.Allows(Tool("bash")));
        Assert.False(reviewer.Allows(Tool("mcp__other__x")));
        Assert.True(SessionRole.Plan.Allows(Tool("skill")));
        Assert.False(SessionRole.Plan.Allows(Tool("edit")));
        Assert.False(SessionRole.Plan.Allows(Tool("agent")));
        Assert.False(SessionRole.Plan.Allows(Tool("mcp__gh__create_issue")));
        // ACP：主 Agent 只协调，写文件与命令交给子 Agent
        Assert.Same(SessionRole.Acp, SessionRole.Resolve("acp", catalog));
        Assert.True(SessionRole.Acp.Allows(Tool("agent")));
        Assert.True(SessionRole.Acp.Allows(Tool("task_create")));
        Assert.True(SessionRole.Acp.Allows(Tool("grep")));
        Assert.False(SessionRole.Acp.Allows(Tool("write")));
        Assert.False(SessionRole.Acp.Allows(Tool("edit")));
        Assert.False(SessionRole.Acp.Allows(Tool("bash")));
        Assert.False(SessionRole.Acp.Allows(Tool("mcp__gh__create_issue")));
    }

    [Fact]
    public void ExternalReminder_AnnouncesModeChanges_WithRuntimeNeutralText()
    {
        Assert.Null(SessionRole.ExternalReminder(SessionRole.Agent, SessionRole.Agent));
        var plan = SessionRole.ExternalReminder(SessionRole.Agent, SessionRole.Plan)!;
        Assert.StartsWith("<system-reminder>\nThis conversation is now in Plan mode.", plan);
        Assert.EndsWith("</system-reminder>", plan);
        Assert.Contains("do not modify the workspace", plan);
        // ACP 的外部版本不提 TKCode 自己的工具名（task_create / agent 在 Claude、Codex 中不存在）
        var acp = SessionRole.ExternalReminder(SessionRole.Plan, SessionRole.Acp)!;
        Assert.Contains("ACP mode", acp);
        Assert.DoesNotContain("task_create", acp);
        Assert.Contains("even though those tools may be available", acp);
        var back = SessionRole.ExternalReminder(SessionRole.Acp, SessionRole.Agent)!;
        Assert.Contains("Agent mode", back);
        Assert.Contains("follow it unless the user says otherwise", back);
        var custom = new SessionRole("reviewer", "Review only.", ["readonly"]);
        Assert.Contains("reviewer mode", SessionRole.ExternalReminder(SessionRole.Agent, custom)!);
        Assert.Null(SessionRole.ExternalReminder(SessionRole.Agent, custom with { Instructions = null }));
    }

    private sealed class NamedTool(string name) : TKCode.Agent.IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public string ParametersSchemaJson => "{}";
        public TKCode.Agent.ToolPermission DescribePermission(System.Text.Json.JsonElement arguments) => new(TKCode.Agent.ToolAccess.None);
        public Task<TKCode.Agent.AgentToolResult> ExecuteAsync(TKCode.Agent.ToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(50);
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
