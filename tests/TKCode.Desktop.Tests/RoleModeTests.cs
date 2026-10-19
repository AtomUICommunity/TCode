using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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

/// <summary>模式（docs/design/composer.md §10.3）：Agent / Plan / ACP 与自定义 role，Shift+Tab 与 / 切换，随会话保存并恢复。</summary>
public sealed class RoleModeTests
{
    [Fact]
    public async Task Modes_SwitchFromChipShortcutAndSlash_AndPersistWithSession()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        var roleFile = Path.Combine(project, ".agents", "roles", "reviewer.md");
        Directory.CreateDirectory(Path.GetDirectoryName(roleFile)!);
        File.WriteAllText(roleFile, "---\nname: Reviewer\ndescription: Read-only code review\ntools: readonly\nmodel: fake/fake-mini\nicon: eye\n---\nReview the code and report issues with file:line references.\n");
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
            var conversation = vm.Conversation;
            var roles = conversation.RolePicker;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => conversation.Picker.Selected is not null && roles.Roles.Any(r => r.Name == "reviewer"), "models and roles");
            foreach (var popup in window.GetVisualDescendants().OfType<Popup>())
            {
                popup.ShouldUseOverlayLayer = true;
            }
            Assert.Equal(["agent", "plan", "acp", "reviewer"], roles.Roles.Select(r => r.Name));
            // 运行时没有子 Agent 时不提供 ACP
            roles.AcpAvailable = false;
            Assert.Equal(["agent", "plan", "reviewer"], roles.Roles.Select(r => r.Name));
            roles.AcpAvailable = true;
            var reviewer = roles.Roles.Single(r => r.Name == "reviewer");
            Assert.Equal(("Reviewer", TKCode.Desktop.Icons.LucideKind.Eye, "项目"), (reviewer.Title, reviewer.Icon, reviewer.Source));

            // 芯片弹层
            var chip = window.FindControl<RolePickerView>("EmptyRolePicker")!;
            chip.Toggle();
            await WaitUntil(() => true, "popup");
            HeadlessApp.Save(window, "role-picker");
            chip.Toggle();

            // Shift+Tab：Agent → Plan；新会话以 Plan 开始，系统提示带模式节
            var editor = window.FindControl<ComposerView>("EmptyComposer")!.FindControl<ComposerEditor>("Input")!;
            editor.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Assert.Equal("plan", roles.Role);
            conversation.ComposerText = "怎么实现缓存？";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "plan turn");
            var session = conversation.Active!;
            Assert.Equal("plan", session.Options.Role);
            Assert.Contains("You are in Plan mode", await File.ReadAllTextAsync(session.FilePath));
            HeadlessApp.Save(window, "role-plan-session");

            // / 选择 ACP：直接切换，不生成 tag
            var docked = window.FindControl<ComposerView>("DockedComposer")!.FindControl<ComposerEditor>("Input")!;
            docked.Focus();
            window.KeyTextInputText("/ac");
            await WaitUntil(() => conversation.Completion.Items.FirstOrDefault()?.Title == "acp", "acp in slash list");
            Assert.True(conversation.Completion.AcceptHighlighted());
            await WaitUntil(() => session.Options.Role == "acp", "acp applied");
            Assert.Equal("", conversation.ComposerText);

            // 自定义 role：同时切换到它指定的模型
            roles.Choose("reviewer");
            await WaitUntil(() => session.Options.Role == "reviewer" && conversation.Picker.Selected?.Model.Id == "fake-mini", "reviewer applied");

            // 重新打开会话：role 按记录恢复
            var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
            conversation.ComposerText = "看一下";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<UserMessageItemViewModel>().Count() == 2, "reviewer turn");
            await server.CloseSessionAsync(session.SessionId);
            var reopened = await server.OpenSessionAsync(session.FilePath);
            Assert.Equal("reviewer", reopened.Session.Options.Role);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
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
