using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>设置 › 命令、Skills 与模式（docs/design/composer.md §14）：列表与来源、被覆盖与问题、按模板新建、默认模式。</summary>
public sealed class AssetsSettingsTests
{
    [Fact]
    public async Task Section_ListsItems_CreatesTemplates_AndSetsDefaultMode()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Write(home, "commands/review.md", "Global review.");
        Write(home, "commands/git/commit.md", "---\ndescription: Write a commit message for the staged changes\n---\nWrite it.");
        Write(project, ".agents/commands/review.md", "---\ndescription: Review with the project's checklist\n---\nReview.");
        Write(home, "skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Extract text and tables from PDF files\n---\n# PDF");
        Write(project, ".agents/skills/nodesc/SKILL.md", "# no description");
        Write(project, ".agents/roles/reviewer.md", "---\nname: Reviewer\ndescription: Read-only code review\ntools: readonly\nmodel: fake/fake-mini\n---\nReview.");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null && vm.Conversation.CurrentCwd is not null, "models");
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav.Single(n => n.Section == SettingsSection.Assets));
            var assets = vm.Settings.Assets;
            await WaitUntil(() => assets.Roles.Count == 1, "assets listed");

            Assert.Equal(["/git:commit", "/review"], assets.Commands.Select(c => c.Title));
            Assert.Equal(("项目", "Review with the project's checklist"), (assets.Commands[1].Source, assets.Commands[1].Description));
            Assert.Equal(["/nodesc", "/pdf"], assets.Skills.Select(c => c.Title));
            var reviewer = Assert.Single(assets.Roles);
            Assert.Equal(("Reviewer  (reviewer)", "tools: readonly · fake/fake-mini"), (reviewer.Title, reviewer.Detail));
            Assert.Contains(assets.Notices, n => n.Text.Contains("review") && n.Path.EndsWith(Path.Combine("commands", "review.md")) && n.Path.StartsWith(home));
            Assert.Contains(assets.Notices, n => n.Text.Contains("no description"));
            Assert.Equal(Path.Combine(project, ".agents"), assets.ProjectDirectory);
            Assert.Equal(["agent", "plan", "acp", "reviewer"], assets.DefaultRoles.Select(r => r.Value));
            await WaitUntil(() => true, "render");
            HeadlessApp.Save(window, "settings-assets");

            // 按模板新建（项目）：文件写入 .agents，Host 监听到变化后列表刷新；模板的 frontmatter 能被解析
            assets.CreateCommand.Execute("role|project");
            var created = Path.Combine(project, ".agents", "roles", "new-mode.md");
            Assert.True(File.Exists(created));
            await WaitUntil(() => assets.Roles.Any(r => r.Dto.Name == "new-mode"), "created role listed", 10_000);
            var mode = assets.Roles.Single(r => r.Dto.Name == "new-mode");
            Assert.Equal("One line shown in the mode picker.", mode.Description);
            Assert.Equal(["all"], mode.Dto.Tools!);
            assets.CreateCommand.Execute("command|global");
            assets.CreateCommand.Execute("command|global");
            Assert.True(File.Exists(Path.Combine(home, "commands", "new-command-2.md")));
            assets.CreateCommand.Execute("skill|global");
            await WaitUntil(() => assets.Skills.Any(s => s.Dto.Name == "new-skill" && s.Description is not null), "created skill listed", 10_000);
            Assert.DoesNotContain(assets.Notices, n => n.Path.Contains("new-"));

            // 默认模式：写入 settings.json，新会话从它开始
            assets.ChooseDefaultRoleCommand.Execute(assets.DefaultRoles.Single(r => r.Value == "plan"));
            await WaitUntil(() => provider.GetRequiredService<UserSettingsService>().Current.DefaultRole == "plan", "default role saved");
            Assert.Contains("\"defaultRole\": \"plan\"", await File.ReadAllTextAsync(Path.Combine(home, "settings.json")));
            Assert.True(assets.DefaultRoles.Single(r => r.Value == "plan").IsSelected);
            vm.Conversation.RolePicker.Choose("acp");
            vm.Conversation.NewChat();
            Assert.Equal("plan", vm.Conversation.RolePicker.Role);
            // 选回 Agent：从 settings.json 删除该字段
            assets.ChooseDefaultRoleCommand.Execute(assets.DefaultRoles.Single(r => r.Value == "agent"));
            await WaitUntil(() => provider.GetRequiredService<UserSettingsService>().Current.DefaultRole is null, "default role cleared");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

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
