using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>
/// 设置页端到端：外部编辑 settings.json → Host 热重载 → 模型选择器出现新厂商；
/// 在设置页切换模型启用 → 写回 settings.json。
/// </summary>
public sealed class SettingsSnapshotTests
{
    [Fact]
    public async Task ExternalEdit_HotReloads_AndUiWritesBack()
    {
        var home = Directory.CreateTempSubdirectory("tcode-settings-home-").FullName;
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("TCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TCode.AgentHost/bin/Debug/net10.0")));
        var settingsPath = Path.Combine(home, "settings.json");
        // 本地 HTTP 服务同时扮演 OpenAI 兼容端点（/v1/models）与 models.dev（/api.json）；端口需在 Host 启动前确定
        var port = FreePort();
        Environment.SetEnvironmentVariable("TCODE_CATALOG_URL", $"http://127.0.0.1:{port}/api.json");

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
            Assert.DoesNotContain(vm.Conversation.Picker.AllModels, m => m.Provider == "anthropic");

            await vm.OpenSettingsCommand.ExecuteAsync(null);
            await Settle();
            HeadlessApp.Save(window, "settings-general");

            // 本地 OpenAI 兼容服务：GET /v1/models 返回两个模型（其中一个在目录中有元数据）
            using var server = new System.Net.HttpListener();
            server.Prefixes.Add($"http://127.0.0.1:{port}/");
            server.Start();
            _ = Task.Run(async () =>
            {
                while (server.IsListening)
                {
                    var context = await server.GetContextAsync();
                    if (context.Request.Url!.AbsolutePath == "/api.json" && context.Request.Headers["If-None-Match"] == "\"fixture\"")
                    {
                        context.Response.StatusCode = 304;
                        context.Response.Close();
                        continue;
                    }
                    if (context.Request.Url!.AbsolutePath == "/api.json")
                    {
                        var catalogBytes = System.Text.Encoding.UTF8.GetBytes(ModelsDevFixture);
                        context.Response.Headers["ETag"] = "\"fixture\"";
                        await context.Response.OutputStream.WriteAsync(catalogBytes);
                        context.Response.Close();
                        continue;
                    }
                    var ok = context.Request.Headers["Authorization"] == "Bearer local-key";
                    context.Response.StatusCode = ok ? 200 : 401;
                    var body = ok ? """{ "data": [ { "id": "deepseek-v4-pro" }, { "id": "local-coder" } ] }""" : """{ "error": { "message": "bad key" } }""";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });

            // 外部编辑：内置厂商只写 Key；自定义厂商不写 models（自动获取）；亮色主题
            File.WriteAllText(settingsPath, $$"""
            {
              "theme": "light",
              "providers": {
                "anthropic": { "apiKey": "sk-ant-test-1234567890" },
                "local": { "name": "本地", "api": "chat", "baseUrl": "http://127.0.0.1:{{port}}/v1", "apiKey": "local-key" }
              }
            }
            """);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Ref == "anthropic/claude-opus-5-5"), "hot reload", 15_000);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Ref == "local/local-coder"), "model discovery", 15_000);
            Assert.Equal(1_000_000, vm.Conversation.Picker.AllModels.Single(m => m.Ref == "local/deepseek-v4-pro").ContextWindows[^1]);

            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);
            await WaitUntil(() => vm.Settings.ConfiguredList.Any(p => p.Id == "local" && p.IsOk), "local provider connected");
            vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(p => p.Id == "local"));
            var local = vm.Settings.Selected!;
            Assert.True(local.SupportsDiscovery);
            Assert.Equal(2, local.Models.Count(m => m.IsEnabled));

            // 测试连接：未保存的错误 Key → 401；清空后用已保存的 Key → 成功
            local.KeyInput = "wrong";
            await local.TestCommand.ExecuteAsync(null);
            Assert.False(local.TestSucceeded);
            local.KeyInput = "";
            await local.TestCommand.ExecuteAsync(null);
            Assert.True(local.TestSucceeded, local.TestResult);
            await Settle();
            HeadlessApp.Save(window, "settings-providers-local");
            // 亮色主题（测试中 App.Services 为空，需要手动挂接 ThemeService）
            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Avalonia.Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            await Settle();
            HeadlessApp.Save(window, "settings-providers-local-light");
            await themes.SetModeAsync(ThemeMode.Dark);

            // 内置厂商：停用 Haiku → models 被写入
            vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(p => p.Id == "anthropic"));
            var anthropic = vm.Settings.Selected!;
            await Settle();
            HeadlessApp.Save(window, "settings-providers");
            anthropic.ToggleModelCommand.Execute(anthropic.Models.Single(m => m.Id == "claude-haiku-5-5"));
            await WaitUntil(() => !vm.Conversation.Picker.AllModels.Any(m => m.Ref == "anthropic/claude-haiku-5-5"), "model disabled", 15_000);
            var text = File.ReadAllText(settingsPath);
            Assert.Contains("\"claude-opus-5-5\": {}", text);
            Assert.DoesNotContain("claude-haiku-5-5", text);

            // 覆盖单个模型的上下文窗口：只写入改动的字段
            anthropic = vm.Settings.Selected!;
            anthropic.EditModelCommand.Execute(anthropic.Models.Single(m => m.Id == "claude-opus-5-5"));
            anthropic.Editing!.ContextInput = "200000";
            anthropic.Editing.SaveCommand.Execute(null);
            await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"contextWindow\": 200000"), "override written");
            Assert.DoesNotContain("\"maxTokens\"", File.ReadAllText(settingsPath));

            // 单独指定协议：保留已有的上下文覆盖，只新增 api
            await WaitUntil(() => vm.Settings.Selected?.Models.FirstOrDefault(m => m.Id == "claude-opus-5-5")?.Model.ContextWindow == 200_000, "override applied");
            anthropic = vm.Settings.Selected!;
            anthropic.EditModelCommand.Execute(anthropic.Models.Single(m => m.Id == "claude-opus-5-5"));
            anthropic.Editing!.ChooseApiCommand.Execute("chat");
            anthropic.Editing.SaveCommand.Execute(null);
            await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"api\": \"chat\""), "api override written");
            Assert.Contains("\"contextWindow\": 200000", File.ReadAllText(settingsPath));
            await WaitUntil(() => vm.Settings.Selected?.Models.FirstOrDefault(m => m.Id == "claude-opus-5-5")?.ApiOverridden == true, "api override shown");
            await Settle();
            HeadlessApp.Save(window, "settings-model-protocol");

            // 添加厂商：打开模板选择框
            vm.Settings.OpenTemplatePickerCommand.Execute(null);
            await Settle();
            Assert.Contains(vm.Settings.TemplateGroups, g => g.Items.Any(t => t.Id == "custom-messages"));
            Assert.DoesNotContain(vm.Settings.TemplateGroups.SelectMany(g => g.Items), t => t.Id == "anthropic"); // 已添加的不再列出
            HeadlessApp.Save(window, "settings-add-provider");

            // 选内置厂商：成为草稿，保存前不写文件；保存后写入
            vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "deepseek"));
            await Settle();
            var draft = vm.Settings.Selected!;
            Assert.True(draft.IsDraft);
            Assert.Equal("deepseek", vm.Settings.ConfiguredList[0].Id);
            Assert.DoesNotContain("deepseek", File.ReadAllText(settingsPath));
            HeadlessApp.Save(window, "settings-draft");
            draft.KeyInput = "sk-deepseek-test";
            draft.SaveCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Provider == "deepseek"), "draft saved", 15_000);
            Assert.Contains("sk-deepseek-test", File.ReadAllText(settingsPath));
            Assert.False(vm.Settings.Selected!.IsDraft);

            // 选自定义模板：进入自定义表单并预选协议
            vm.Settings.OpenTemplatePickerCommand.Execute(null);
            vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "custom-messages"));
            Assert.True(vm.Settings.IsCreatingCustom);
            Assert.Equal("messages", vm.Settings.Protocols.Single(p => p.IsSelected).Value);

            // 从 “models.dev” 同步目录：Anthropic 的模型元数据随之更新
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[0]);
            await vm.Settings.RefreshCatalogStatusAsync();
            Assert.Equal("embedded", vm.Settings.Catalog!.Source);
            // 检查更新：只下载并比较，不改变当前目录
            await vm.Settings.CheckCatalogCommand.ExecuteAsync(null);
            Assert.True(vm.Settings.HasPendingUpdate);
            Assert.Equal("embedded", vm.Settings.Catalog!.Source);
            Assert.Contains(vm.Conversation.Picker.AllModels, m => m.Ref == "anthropic/claude-opus-5-5" && m.Name == "Claude Opus 5.5");
            // 默认只看已添加的厂商：Anthropic 的 Opus 改名、其余模型被移除
            var anthropicChange = Assert.Single(vm.Settings.CatalogChanges, c => c.Dto.ProviderId == "anthropic");
            Assert.Contains(anthropicChange.Models, m => m.IsChanged && m.Changes.Any(c => c.NewText == "Claude Opus 5.5 (synced)"));
            Assert.Contains(anthropicChange.Models, m => m.IsRemoved);
            Assert.All(vm.Settings.CatalogChanges, c => Assert.True(c.IsConfigured));
            await Settle();
            HeadlessApp.Save(window, "settings-catalog-diff");
            vm.Settings.ShowOnlyConfiguredChanges = false;
            Assert.Contains(vm.Settings.CatalogChanges, c => !c.IsConfigured);

            // 应用更新
            await vm.Settings.ApplyCatalogUpdateCommand.ExecuteAsync(null);
            Assert.Equal("synced", vm.Settings.Catalog!.Source);
            Assert.False(vm.Settings.HasPendingUpdate);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.FirstOrDefault(m => m.Ref == "anthropic/claude-opus-5-5")?.Name == "Claude Opus 5.5 (synced)",
                "synced metadata", 15_000);
            await Settle();
            HeadlessApp.Save(window, "settings-catalog-synced");
            // 再次检查：ETag 未变 → 已是最新（本地服务对 If-None-Match 返回 304）
            await vm.Settings.CheckCatalogCommand.ExecuteAsync(null);
            Assert.True(vm.Settings.IsUpToDate);
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);

            // 错误的文件：保留旧配置并显示错误
            File.WriteAllText(settingsPath, """{ "providers": { "anthropic": """);
            await WaitUntil(() => vm.Settings.HasErrors, "error shown", 15_000);
            Assert.Contains(vm.Conversation.Picker.AllModels, m => m.Ref == "anthropic/claude-opus-5-5");
            HeadlessApp.Save(window, "settings-error");
            server.Stop();

            vm.CloseSettingsCommand.Execute(null);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<AgentHostConnection>().Dispose();
    }

    /// <summary>models.dev 格式的最小目录：Anthropic 的 Opus 5.5 与 DeepSeek。</summary>
    private const string ModelsDevFixture = """
    {
      "anthropic": { "id": "anthropic", "name": "Anthropic", "npm": "@ai-sdk/anthropic", "env": ["ANTHROPIC_API_KEY"],
        "models": { "claude-opus-5-5": { "id": "claude-opus-5-5", "name": "Claude Opus 5.5 (synced)", "tool_call": true, "reasoning": true,
          "limit": { "context": 1000000, "output": 128000 }, "modalities": { "input": ["text", "image"], "output": ["text"] } } } },
      "deepseek": { "id": "deepseek", "name": "DeepSeek", "npm": "@ai-sdk/openai-compatible", "api": "https://api.deepseek.com", "env": ["DEEPSEEK_API_KEY"],
        "models": { "deepseek-v4-pro": { "id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro", "tool_call": true, "limit": { "context": 1000000, "output": 393216 } } } }
    }
    """;

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
