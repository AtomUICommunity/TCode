using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Tests;

/// <summary>真实 Host（假模型）加上完整的 DI 容器，参照 <see cref="PromptQueueTests"/>。</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestApp(ServiceProvider provider)
    {
        _provider = provider;
    }

    public ConversationViewModel Conversation => _provider.GetRequiredService<ConversationViewModel>();

    public DraftStore Drafts => _provider.GetRequiredService<DraftStore>();

    public IServiceProvider Services => _provider;

    public string Home { get; private init; } = "";

    /// <param name="home">沿用已有的数据目录（模拟重启应用）；null 为新建一个。</param>
    public static Task<TestApp> StartAsync(string? home = null)
    {
        home ??= Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
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
        return Task.FromResult(new TestApp(services.BuildServiceProvider()) { Home = home });
    }

    public async Task Send(string text)
    {
        Conversation.ComposerText = text;
        await Conversation.SendCommand.ExecuteAsync(null);
    }

    public async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
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

    public async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
