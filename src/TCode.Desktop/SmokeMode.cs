using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia.Rendering;
using StreamMark.Avalonia.Transcript;
using TCode.Desktop.Controls;
using TCode.Desktop.Controls.Composer;
using TCode.Desktop.Previews;
using TCode.Desktop.Services;
using TCode.Desktop.ViewModels;
using TCode.Desktop.Views;

namespace TCode.Desktop;

/// <summary>
/// 冒烟模式（<c>TCode --smoke [--out=frame.png]</c>）：在发布后的二进制（含 NativeAOT）里以无头平台运行真实的应用代码——
/// 启动 Agent Host、用 Fake Provider 发送一条消息、等待 Agent 循环与流式 Markdown 渲染完成、展开思考过程，
/// 检查会话视图的结果并截图。不产生任何系统级输入，用于验证只在发布构建中才会出现的问题。
/// </summary>
internal static class SmokeMode
{
    public static int Run(string[] args)
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : " — " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        var home = Directory.CreateTempSubdirectory("tcode-smoke-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tcode-smoke-project-").FullName;
        File.WriteAllText(Path.Combine(project, "README.md"), "# smoke\n\nA **sample** project with `code`.\n");
        Directory.CreateDirectory(Path.Combine(project, ".agents", "commands"));
        File.WriteAllText(Path.Combine(project, ".agents", "commands", "hello.md"), "---\ndescription: Say hello\n---\nSay hello.\n");
        Directory.CreateDirectory(Path.Combine(project, ".agents", "skills", "greet"));
        File.WriteAllText(Path.Combine(project, ".agents", "skills", "greet", "SKILL.md"), "---\nname: greet\ndescription: Greeting rules\n---\nBe brief.\n");
        Environment.SetEnvironmentVariable("TCODE_HOME", home);
        Environment.SetEnvironmentVariable("TCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TCODE_EXTERNAL_RUNTIMES", "0");
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        App.Services = provider;

        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();

            Check("models.loaded", WaitUntil(() => vm.Conversation.Picker.Selected is not null), vm.Conversation.Picker.Selected?.ToString());

            vm.Conversation.ComposerText = "看看目录里有哪些文件";
            vm.Conversation.SendCommand.Execute(null);
            var finished = WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 });
            Check("conversation.finished", finished, $"{vm.Conversation.Active?.Items.Count ?? 0} items");

            var assistant = vm.Conversation.Active?.Items.OfType<AssistantMessageItemViewModel>().FirstOrDefault();
            var texts = assistant?.Blocks.OfType<TextBlockViewModel>().ToList() ?? [];
            Check("reply.text", texts.Any(t => t.Text.Length > 0), $"{texts.Count} text blocks");

            // 平滑输出放完积压后，流式文档应进入最终状态
            Check("markdown.complete", WaitUntil(() => texts.Count > 0 && texts.All(t => t.Stream?.Document.Latest.IsComplete ?? true)), null);

            // 展开思考过程：之后的行必须被推下去，不能重叠
            assistant?.Blocks.OfType<ThinkingBlockViewModel>().FirstOrDefault()?.ToggleCommand.Execute(null);
            Pump(20);
            var transcript = window.GetVisualDescendants().OfType<TranscriptView>().FirstOrDefault();
            Check("transcript.view", transcript is not null, null);
            if (transcript is not null)
            {
                var panel = transcript.Panel;
                Check("transcript.rows", panel.RowCount >= 3 && panel.RealizedCount > 0, $"{panel.RealizedCount}/{panel.RowCount} rows realized");
                var rows = panel.Children.Where(c => c.IsVisible).OrderBy(c => c.Bounds.Top).ToList();
                var overlaps = rows.Zip(rows.Skip(1)).Count(p => p.Second.Bounds.Top < p.First.Bounds.Bottom - 0.5);
                Check("transcript.no-overlap", overlaps == 0, $"{overlaps} overlapping rows");
                Check("markdown.rendered", window.GetVisualDescendants().OfType<TextSegmentView>().Any(s => s.Layout is not null), null);
            }

            CheckFilePreviews(Check, vm, window, project);
            CheckComposer(Check, window);

            // @ 文件：Host 的文件索引（rg 或内置遍历）与 workspace/searchFiles 在 AOT 下可用
            var search = Task.Run(async () =>
            {
                var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
                return await server.SearchFilesAsync(new TCode.Rpc.FileSearchRequest(project, "readme", 10));
            });
            // command / skill / role：frontmatter 解析与 assets/list 在 AOT 下可用
            var assets = Task.Run(async () =>
            {
                var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
                return await server.ListAssetsAsync(project);
            });
            Check("assets.list", WaitUntil(() => assets.IsCompleted) && assets.Result.Commands.Any(c => c.Name == "hello" && c.Description == "Say hello"),
                assets.IsCompletedSuccessfully ? string.Join(", ", assets.Result.Commands.Select(c => c.Name)) : assets.Exception?.GetBaseException().Message);
            // command：发送时 Host 展开 .md 正文；用户消息的显示片段经会话记录（AiJsonContext）保存
            vm.Conversation.Composer.SetContent([new TagSegment(TagKind.Command, "hello", "hello"), new TextSegment(" world "),
                new TagSegment(TagKind.Skill, "greet", "greet")]);
            vm.Conversation.SendCommand.Execute(null);
            var commandTurn = WaitUntil(() => vm.Conversation.Active is { IsRunning: false } a && a.Items.OfType<UserMessageItemViewModel>().Count() == 2);
            var commandMessage = vm.Conversation.Active?.Items.OfType<UserMessageItemViewModel>().LastOrDefault();
            Check("command.expanded", commandTurn && commandMessage is { HasSegments: true } &&
                commandMessage.Text.Contains("<skill name=\"greet\"", StringComparison.Ordinal) && commandMessage.Text.EndsWith("Say hello.\n\nworld /greet", StringComparison.Ordinal),
                commandMessage?.Text);
            // 模式：Host 解析 role、过滤工具并写入会话记录
            vm.Conversation.RolePicker.Choose("plan");
            var planApplied = WaitUntil(() => vm.Conversation.Active?.Options.Role == "plan");
            Check("role.plan", planApplied, vm.Conversation.Active?.Options.Role);
            Check("files.search", WaitUntil(() => search.IsCompleted) && search.Result.Items.FirstOrDefault()?.Path == "README.md",
                search.IsCompletedSuccessfully ? string.Join(", ", search.Result.Items.Select(i => i.Path)) : search.Exception?.GetBaseException().Message);

            using var frame = window.CaptureRenderedFrame();
            Check("ui.frame", frame is not null, null);
            var output = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?["--out=".Length..];
            if (frame is not null && output is not null)
            {
                using var file = File.Create(output);
                frame.Save(file, new PngBitmapEncoderOptions());
                Console.WriteLine($"saved {output}");
            }
            window.Close();
        }
        catch (Exception ex)
        {
            Check("smoke", false, ex.ToString());
        }

        Console.WriteLine(failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // 输入框：tag 排版与绘制、剪贴板内部格式（源生成 JSON）在 AOT 下都要可用
    private static void CheckComposer(Action<string, bool, string?> check, MainWindow window)
    {
        var editor = window.GetVisualDescendants().OfType<ComposerEditor>().FirstOrDefault(e => e.IsEffectivelyVisible);
        check("composer.editor", editor is not null, null);
        if (editor is null)
        {
            return;
        }
        var tag = new TagSegment(TagKind.File, "README.md", "README.md");
        editor.Document.SetContent([new TextSegment("看下 "), tag, new TextSegment(" 的内容")]);
        Pump(5);
        check("composer.tag-width", editor.CaretBounds(4).X - editor.CaretBounds(3).X > 20, null);
        var json = ComposerClipboard.Serialize(editor.Document.Segments);
        var restored = ComposerClipboard.Deserialize(json);
        check("composer.clipboard-json", restored is not null && restored.SequenceEqual(editor.Document.Segments), json);
        editor.Document.SetContent([]);
    }

    // 文件预览：图片（含动图解码与 SVG 栅格化）与源码着色在 AOT 下都要可用
    private static void CheckFilePreviews(Action<string, bool, string?> check, MainWindowViewModel vm, MainWindow window, string project)
    {
        using (var bitmap = new SkiaSharp.SKBitmap(32, 16))
        {
            bitmap.Erase(new SkiaSharp.SKColor(0x4F, 0x8C, 0xFF, 0x80));
            using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(project, "logo.png"), encoded.ToArray());
        }
        // 两帧 2×2 动图
        File.WriteAllBytes(Path.Combine(project, "anim.gif"), Convert.FromBase64String(
            "R0lGODlhAgACAIEAAAAAAP8AAAD/AAAA/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgACAAACBAzFMQUAIfkEAAoAAAAsAAAAAAIAAgAAAgQcQ3EFADs="));
        File.WriteAllText(Path.Combine(project, "icon.svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect width="24" height="12" fill="#4F8CFF"/></svg>""");
        File.WriteAllText(Path.Combine(project, "data.json"), "{\n  \"name\": \"smoke\",\n  \"ok\": true\n}\n");

        FilePreviewViewModel? Open(string name)
        {
            vm.Workbench.OpenFile(Path.Combine(project, name));
            WaitUntil(() => vm.Workbench.ActiveFile is { } file && (file.Preview is not null || file.HasNotice));
            return vm.Workbench.ActiveFile?.Preview;
        }

        var png = Open("logo.png") as ImagePreviewViewModel;
        check("preview.png", png is { PixelSize.Width: 32, PixelSize.Height: 16 }, vm.Workbench.ActiveFile?.Notice ?? png?.Info);
        var gif = Open("anim.gif") as ImagePreviewViewModel;
        check("preview.gif", gif is { IsAnimated: true, Frames.Count: 2 }, vm.Workbench.ActiveFile?.Notice ?? gif?.Info);
        var svg = Open("icon.svg") as ImagePreviewViewModel;
        check("preview.svg", svg is { IsVector: true, PixelSize.Width: 24 } && vm.Workbench.ActiveFile!.HasViewSwitch, vm.Workbench.ActiveFile?.Notice ?? svg?.Info);
        var json = Open("data.json") as CodePreviewViewModel;
        var highlighted = WaitUntil(() => window.GetVisualDescendants().OfType<CodeView>().FirstOrDefault() is { IsHighlighted: true });
        check("preview.code", json is { Language: "json" } && highlighted, vm.Workbench.ActiveFile?.Notice);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            Pump(1);
        }
        Pump(5);
        return true;
    }

    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }
}
