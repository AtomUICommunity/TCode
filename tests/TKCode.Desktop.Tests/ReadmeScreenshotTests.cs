using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// README 截图：预置一个示例项目和几段会话（JSONL），以 2 倍缩放无头渲染主要界面。
/// 只在设置 TKCODE_README_SCREENSHOTS=&lt;目录&gt; 时运行（tools/readme-screenshots.sh），不参与常规测试。
/// 设备名用 TKCODE_DEVICE_NAME 固定，模型用 settings.json 里的占位 Key，不发出任何真实请求。
/// </summary>
public sealed class ReadmeScreenshotTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh")]
    public async Task Capture(string lang)
    {
        var output = Environment.GetEnvironmentVariable("TKCODE_README_SCREENSHOTS");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Set TKCODE_README_SCREENSHOTS to regenerate the README screenshots.");
        var zh = lang == "zh";

        var home = Directory.CreateTempSubdirectory("tkcode-readme-home-").FullName;
        var project = Path.Combine(Directory.CreateTempSubdirectory("tkcode-readme-").FullName, "weather-api");
        WriteProject(project);
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net10.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "0");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_DEVICE_NAME", "MacBook Pro");
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
            {
              "model": "anthropic/claude-opus-5-5",
              "language": "{{(zh ? "zh-CN" : "en-US")}}",
              "theme": "dark",
              "providers": {
                "anthropic": { "apiKey": "sk-ant-readme-placeholder" },
                "openai": { "apiKey": "sk-readme-placeholder" }
              }
            }
            """);
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Save();
        WriteSessions(home, project, zh);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Avalonia.Application.Current!);
            await themes.SetModeAsync(ThemeMode.Dark);
            if (vm.IsChinese != zh)
            {
                vm.ToggleLanguageCommand.Execute(null);
            }
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.SetRenderScaling(2);
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            await WaitUntil(() => vm.Sidebar.VisibleSessions.Count == 5, "sessions listed");
            void Save(string name) => SaveTo(window, output!, $"{name}-{lang}");

            Save("home");

            await vm.Conversation.OpenSessionCommand.ExecuteAsync(vm.Conversation.Sessions[0]);
            await WaitUntil(() => vm.Conversation.Active is { Items.Count: >= 2 }, "session opened");
            await Settle(20);
            Save("conversation");

            vm.ToggleExplorerCommand.Execute(null);
            vm.Workbench.OpenFile(Path.Combine(project, "src", "WeatherApi", "Endpoints", "ForecastEndpoints.cs"));
            await WaitUntil(() => vm.Workbench.ActiveFile?.Preview is TKCode.Desktop.Previews.CodePreviewViewModel, "file loaded");
            await Settle(20);
            Save("workbench");
            vm.ToggleExplorerCommand.Execute(null);
            vm.ToggleWorkbenchCommand.Execute(null);
            await Settle();

            vm.Palette.Open();
            await Settle();
            Save("palette");
            vm.Palette.Close();

            await themes.SetModeAsync(ThemeMode.Light);
            await Settle(20);
            Save("conversation-light");
            await themes.SetModeAsync(ThemeMode.Dark);

            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<AgentHostConnection>().Dispose();
    }

    private static void SaveTo(Avalonia.Controls.TopLevel window, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private const string EndpointBefore = """
        using Microsoft.Extensions.Caching.Memory;
        using WeatherApi.Upstream;

        namespace WeatherApi.Endpoints;

        public static class ForecastEndpoints
        {
            public static void MapForecast(this WebApplication app)
            {
                app.MapGet("/forecast", GetForecastAsync);
            }

            private static async Task<IResult> GetForecastAsync(string city, IForecastClient client, CancellationToken ct)
            {
                var forecast = await client.GetAsync(city, ct);
                return Results.Ok(forecast);
            }
        }

        """;

    private const string EndpointAfter = """
        using Microsoft.Extensions.Caching.Memory;
        using WeatherApi.Upstream;

        namespace WeatherApi.Endpoints;

        public static class ForecastEndpoints
        {
            private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

            public static void MapForecast(this WebApplication app)
            {
                app.MapGet("/forecast", GetForecastAsync);
            }

            private static async Task<IResult> GetForecastAsync(string city, IForecastClient client, IMemoryCache cache,
                CancellationToken ct)
            {
                var forecast = await cache.GetOrCreateAsync($"forecast:{city.ToLowerInvariant()}", entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                    return client.GetAsync(city, ct);
                });
                return Results.Ok(forecast);
            }
        }

        """;

    private static void WriteProject(string project)
    {
        var endpoints = Path.Combine(project, "src", "WeatherApi", "Endpoints");
        Directory.CreateDirectory(endpoints);
        Directory.CreateDirectory(Path.Combine(project, "src", "WeatherApi", "Upstream"));
        Directory.CreateDirectory(Path.Combine(project, "tests", "WeatherApi.Tests"));
        File.WriteAllText(Path.Combine(endpoints, "ForecastEndpoints.cs"), EndpointAfter);
        File.WriteAllText(Path.Combine(endpoints, "HealthEndpoints.cs"), "namespace WeatherApi.Endpoints;\n");
        File.WriteAllText(Path.Combine(project, "src", "WeatherApi", "Upstream", "ForecastClient.cs"), "namespace WeatherApi.Upstream;\n");
        File.WriteAllText(Path.Combine(project, "src", "WeatherApi", "Program.cs"), "var builder = WebApplication.CreateBuilder(args);\n");
        File.WriteAllText(Path.Combine(project, "tests", "WeatherApi.Tests", "ForecastTests.cs"), "namespace WeatherApi.Tests;\n");
        File.WriteAllText(Path.Combine(project, "README.md"), "# weather-api\n");
        File.WriteAllText(Path.Combine(project, "WeatherApi.slnx"), "<Solution />\n");
        Git(project, "init", "-q", "-b", "main");
        Git(project, "-c", "user.email=demo@example.com", "-c", "user.name=demo", "add", ".");
        Git(project, "-c", "user.email=demo@example.com", "-c", "user.name=demo", "commit", "-q", "-m", "init");
    }

    private static void WriteSessions(string home, string project, bool zh)
    {
        var dir = Path.Combine(home, "sessions", "--readme--");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.Now;
        string[] others = zh
            ? ["修复 HttpClientTests 里不稳定的重试测试", "解释一下限流中间件是怎么工作的", "升级到 .NET 10 并清理编译警告", "起草 v1.4 的发布说明"]
            : ["Fix the flaky retry test in HttpClientTests", "Explain how the rate limiter middleware works", "Upgrade to .NET 10 and clean up warnings",
               "Draft release notes for v1.4"];
        for (var i = 0; i < others.Length; i++)
        {
            var at = now.AddHours(-(i + 1) * 5);
            WriteSession(dir, project, at, [User(others[i], at)]);
        }
        WriteSession(dir, project, now.AddMinutes(-3), MainConversation(zh, now.AddMinutes(-6)));
    }

    private static void WriteSession(string dir, string project, DateTimeOffset at, IReadOnlyList<JsonObject> messages)
    {
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(dir, $"{at.UtcDateTime:yyyyMMddTHHmmss}_{id}.jsonl");
        var lines = new List<string>
        {
            new JsonObject { ["type"] = "session", ["sessionId"] = id, ["cwd"] = project, ["version"] = 1 }.ToJsonString(),
            new JsonObject { ["type"] = "modelChange", ["id"] = "mc", ["provider"] = "anthropic", ["modelId"] = "claude-opus-5-5" }.ToJsonString(),
        };
        string? parent = "mc";
        for (var i = 0; i < messages.Count; i++)
        {
            var entry = new JsonObject { ["type"] = "message", ["id"] = $"e{i}", ["parentId"] = parent, ["message"] = messages[i] };
            lines.Add(entry.ToJsonString());
            parent = $"e{i}";
        }
        File.WriteAllLines(file, lines);
        File.SetLastWriteTimeUtc(file, at.UtcDateTime);
    }

    private static List<JsonObject> MainConversation(bool zh, DateTimeOffset at)
    {
        const string path = "src/WeatherApi/Endpoints/ForecastEndpoints.cs";
        var diff = Diff(path);
        var t = at;
        DateTimeOffset Next() => t = t.AddSeconds(8);
        return
        [
            User(zh ? "给 GET /forecast 加上 10 分钟的缓存，并确认测试仍然通过。" : "Add a 10-minute cache to GET /forecast and make sure the tests still pass.", t),
            Assistant(Next(),
                Thinking(zh ? "先找到 /forecast 的处理函数，再看 IMemoryCache 是否已经注册。" : "Find the /forecast handler first, then check whether IMemoryCache is already registered."),
                Text(zh ? "我先找到这个接口的实现。" : "Let me find where the endpoint is defined."),
                Call("c1", "grep", new JsonObject { ["pattern"] = "MapGet\\(\"/forecast\"", ["path"] = "src" })),
            Result("c1", "grep", $"{path}:10:        app.MapGet(\"/forecast\", GetForecastAsync);", Next()),
            Assistant(Next(), Call("c2", "read", new JsonObject { ["path"] = path })),
            Result("c2", "read", EndpointBefore, Next()),
            Assistant(Next(),
                Text(zh
                    ? "每次请求都会直接调用上游接口。我用 `IMemoryCache` 按城市缓存结果，10 分钟后过期。"
                    : "Every request hits the upstream provider. I'll cache results per city with `IMemoryCache` and expire them after 10 minutes."),
                Call("c3", "edit", new JsonObject { ["path"] = path, ["edits"] = new JsonArray() })),
            Result("c3", "edit", $"Replaced 1 block(s) in {path}.", Next(),
                new JsonObject { ["kind"] = "diff", ["path"] = path, ["diff"] = diff, ["added"] = 9, ["removed"] = 2, ["firstChangedLine"] = 8 }),
            Assistant(Next(), Call("c4", "bash", new JsonObject { ["command"] = "dotnet test" })),
            Result("c4", "bash", "  Determining projects to restore...\n  WeatherApi -> bin/Debug/net10.0/WeatherApi.dll\n  WeatherApi.Tests -> bin/Debug/net10.0/WeatherApi.Tests.dll\n\nPassed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24, Duration: 1.8 s",
                Next(), new JsonObject { ["kind"] = "command", ["command"] = "dotnet test", ["exitCode"] = 0 }),
            Assistant(Next(), Text(zh
                ? """
                  完成。`GET /forecast` 现在会按城市缓存 10 分钟：

                  | | 修改前 | 修改后 |
                  |---|---|---|
                  | 上游调用（同一城市 100 次请求） | 100 | 1 |
                  | 测试 | 24 通过 | 24 通过 |

                  ```csharp
                  var forecast = await cache.GetOrCreateAsync($"forecast:{city.ToLowerInvariant()}", entry =>
                  {
                      entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                      return client.GetAsync(city, ct);
                  });
                  ```

                  缓存键统一转成小写，`Paris` 和 `paris` 会命中同一条缓存。
                  """
                : """
                  Done. `GET /forecast` now caches each city's forecast for 10 minutes:

                  | | Before | After |
                  |---|---|---|
                  | Upstream calls (100 requests, one city) | 100 | 1 |
                  | Tests | 24 passed | 24 passed |

                  ```csharp
                  var forecast = await cache.GetOrCreateAsync($"forecast:{city.ToLowerInvariant()}", entry =>
                  {
                      entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                      return client.GetAsync(city, ct);
                  });
                  ```

                  The cache key is lower-cased, so `Paris` and `paris` share one entry.
                  """)),
        ];
    }

    private static string Diff(string path)
    {
        var before = EndpointBefore.Split('\n');
        var after = EndpointAfter.Split('\n');
        var lines = new List<string> { $"--- a/{path}", $"+++ b/{path}", "@@ -5,14 +5,21 @@" };
        lines.AddRange(before[4..7].Select(l => " " + l));
        lines.Add("+" + after[6]);
        lines.Add("+");
        lines.AddRange(before[7..12].Select(l => " " + l));
        lines.Add("-" + before[12]);
        lines.AddRange(after[14..16].Select(l => "+" + l));
        lines.Add(" " + before[13]);
        lines.Add("-" + before[14]);
        lines.AddRange(after[17..22].Select(l => "+" + l));
        lines.AddRange(before[15..18].Select(l => " " + l));
        return string.Join('\n', lines) + "\n";
    }

    private static JsonObject User(string text, DateTimeOffset at) => new()
    {
        ["role"] = "user",
        ["timestamp"] = at.ToString("O"),
        ["content"] = new JsonArray(Text(text)),
    };

    private static JsonObject Assistant(DateTimeOffset at, params JsonObject[] content) => new()
    {
        ["role"] = "assistant",
        ["timestamp"] = at.ToString("O"),
        ["content"] = new JsonArray(content.Cast<JsonNode>().ToArray()),
        ["api"] = "messages",
        ["provider"] = "anthropic",
        ["model"] = "claude-opus-5-5",
        ["usage"] = new JsonObject { ["input"] = 18400, ["output"] = 620, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
        ["stopReason"] = content.Any(c => (string?)c["type"] == "toolCall") ? "ToolUse" : "Stop",
    };

    private static JsonObject Result(string id, string tool, string text, DateTimeOffset at, JsonObject? details = null)
    {
        var result = new JsonObject
        {
            ["role"] = "toolResult",
            ["timestamp"] = at.ToString("O"),
            ["toolCallId"] = id,
            ["toolName"] = tool,
            ["content"] = new JsonArray(Text(text)),
        };
        if (details is not null)
        {
            result["details"] = details.ToJsonString();
        }
        return result;
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Thinking(string text) => new() { ["type"] = "thinking", ["thinking"] = text };

    private static JsonObject Call(string id, string name, JsonObject args) =>
        new() { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["argumentsJson"] = args.ToJsonString() };

    private static void Git(string cwd, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
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

    private static async Task Settle(int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
