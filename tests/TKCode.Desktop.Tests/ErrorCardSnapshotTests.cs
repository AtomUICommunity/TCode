using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>请求失败的错误卡片：服务端返回的长 JSON 错误必须在卡片内换行，不能被截断到窗口外。</summary>
public sealed class ErrorCardSnapshotTests
{
    private const string LongError =
        """400 {"code":11158,"msg":"model [grok-4.7] is not allowed for user type [personal_free_user]","requestId":"85e58821faca86a9f462434c0c7b53ab","displayMsg":{"en":"Your account type does not have access to this model. Upgrade your plan or choose another model.","zh":"您的账号类型无权使用该模型，请升级套餐或选择其他模型。"}}""";

    [Fact]
    public async Task LongError_WrapsInsideCard()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            session.Apply(new AgentStarted());
            session.Apply(new MessageStarted("u2", "user", "左边会话增加加载数量限制"));
            session.Apply(new MessageStarted("m-err", "assistant", null));
            session.Apply(new MessageEnded("m-err", "assistant", "Error", LongError, null));
            await Settle();

            var text = window.GetVisualDescendants().OfType<SelectableTextBlock>().Last(t => t.Text == LongError);
            Assert.True(text.IsEffectivelyVisible);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            // 换行后高度至少两行，且右边缘不超出窗口
            Assert.True(text.Bounds.Height >= text.LineHeight * 2, $"error text is not wrapped (height {text.Bounds.Height})");
            var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= window.Width, $"error text overflows the window (right edge {right})");
            HeadlessApp.Save(window, "error-card");
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
            await Task.Delay(50);
        }
        await Settle();
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
