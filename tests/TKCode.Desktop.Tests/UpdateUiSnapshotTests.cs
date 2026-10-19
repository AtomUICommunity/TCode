using System.Security.Cryptography;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TKCode.Desktop.Services;
using TKCode.Desktop.Updates;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 更新的界面（auto-update.md §9）：右下角卡片的各个状态、设置 › 关于的「更新」一节（StreamMark 渲染更新说明）、
/// 设置 › 通用的「关闭窗口时保留在托盘」、托盘菜单内容。截图保存到 snapshots/update-*.png。
/// </summary>
public sealed class UpdateUiSnapshotTests
{
    private const string Notes = "## 新功能\n\n- 应用内更新：启动 60 秒后检查，**后台下载**，安装并重启\n- 系统托盘\n\n## 修复\n\n- 终端搜索高亮在滚动后错位（`#123`）";

    [Fact]
    public async Task Card_And_About_States()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-update-ui-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new UpdateTests.FakeReleases(key);
        server.Add("v1.2.0", notes: Notes);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        // 替换为指向假 GitHub 的更新服务（Windows 安装方式：下载后不需要平台校验；测试中不会真正启动安装程序）
        services.AddSingleton(sp => new UpdateService(sp.GetRequiredService<StateService>(), sp.GetRequiredService<UserSettingsService>(), NullLogger.Instance,
            new UpdateServiceOptions
            {
                TimeProvider = new FakeTimeProvider(),
                CurrentVersion = SemVersion.Parse("1.1.0"),
                Rid = "linux-x64",
                Target = new InstallTarget.WindowsInstaller(home),
                UpdatesDirectory = Path.Combine(home, "updates"),
                FeedOverride = "https://feed.test",
                PublicKeys = [key.ExportSubjectPublicKeyInfoPem()],
                HttpClient = new HttpClient(server),
            }));
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var updates = vm.Updates;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await Settle();

            // 自动检查发现新版本：卡片出现，设置按钮上有小圆点
            await updates.Service.CheckAsync(manual: false);
            await Settle();
            Assert.True(updates.IsCardVisible);
            Assert.Equal(UpdateCardMode.Available, updates.CardMode);
            Assert.True(updates.HasUpdate);
            HeadlessApp.Save(window, "update-card-available");
            Assert.Contains(AppTray.BuildItems(updates), i => i.Action == TrayAction.Download && i.Text!.Contains("1.2.0"));

            // 「更新」：下载 → 已就绪
            await updates.DownloadCommand.ExecuteAsync(null);
            await Settle();
            Assert.Equal(UpdateCardMode.Ready, updates.CardMode);
            HeadlessApp.Save(window, "update-card-ready");
            Assert.Contains(AppTray.BuildItems(updates), i => i.Action == TrayAction.Install);

            // 「安装并重启」时有会话在运行：先确认，可以取消
            var busy = new SessionItemViewModel("s1", Path.Combine(home, "s1.jsonl"), home) { IsRunning = true };
            vm.Conversation.Sessions.Add(busy);
            await updates.InstallCommand.ExecuteAsync(null);
            await Settle();
            Assert.Equal(UpdateCardMode.Confirm, updates.CardMode);
            Assert.Equal(1, updates.RunningCount);
            HeadlessApp.Save(window, "update-card-confirm");
            updates.CancelInstallCommand.Execute(null);
            Assert.Equal(UpdateCardMode.Ready, updates.CardMode);
            vm.Conversation.Sessions.Remove(busy);

            // 卡片的「更新说明」打开设置 › 关于：状态、通道、自动检查、StreamMark 渲染的更新说明
            await updates.ShowNotesCommand.ExecuteAsync(null);
            await Settle();
            Assert.True(vm.IsSettingsOpen);
            Assert.True(vm.Settings.IsAbout);
            Assert.False(updates.IsCardVisible);
            Assert.True(updates.HasNotes);
            HeadlessApp.Save(window, "update-settings-about");

            // 预发布版本（默认接收）与自动检查写回 settings.json
            Assert.True(updates.IsReceivingPrereleases);
            updates.IsReceivingPrereleases = false;
            updates.IsAutoCheck = false;
            await Settle();
            var settingsText = await File.ReadAllTextAsync(Path.Combine(home, "settings.json"));
            Assert.Contains("\"prerelease\": false", settingsText);
            Assert.Contains("\"autoCheck\": false", settingsText);
            Assert.False(updates.IsReceivingPrereleases);
            Assert.Equal(UpdateChannel.Stable, updates.Service.Channel);
            Assert.False(updates.IsAutoCheck);

            // 下载失败：卡片显示原因与「重试」
            updates.Service.Later();
            server.Fail = true;
            await updates.Service.CheckNowAsync();
            await Settle();
            // 检查失败不影响已下载的版本：仍可安装，状态行附上失败原因
            Assert.Equal(UpdateStatus.Ready, updates.Service.Status);
            Assert.True(updates.ShowInstallButton);
            Assert.Contains("network down", updates.StatusText);
            HeadlessApp.Save(window, "update-settings-about-failed");

            // 设置 › 通用：关闭窗口时保留在托盘（默认开启）
            await vm.Settings.OpenAsync(SettingsSection.General);
            await Settle();
            Assert.True(vm.Settings.CloseToTray);
            HeadlessApp.Save(window, "update-settings-general-tray");
            vm.Settings.CloseToTray = false;
            Assert.Contains("\"closeToTray\": false", await File.ReadAllTextAsync(Path.Combine(home, "settings.json")));
            return true;
        }, CancellationToken.None);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
