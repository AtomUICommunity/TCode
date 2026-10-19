using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class ModelSettingsTests
{
    [Fact]
    public void ModelChoices_PersistIndependently_AndFollowSessionRemovesFastModel()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-model-settings-").FullName, "settings.json");
        File.WriteAllText(path, """{ "theme": "dark", "providers": { "gateway": { "apiKey": "keep" } } }""");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);
        settings.SetDefaultModel("gateway/main");
        settings.SetFastModel("gateway/fast");
        Assert.Equal("gateway/main", settings.Current.Model);
        Assert.Equal("gateway/fast", settings.Current.FastModel);
        Assert.Empty(settings.Errors);

        settings.SetFastModel(null);
        Assert.Null(settings.Current.FastModel);
        Assert.Equal("gateway/main", settings.Current.Model);
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(saved["fastModel"]);
        Assert.Equal("dark", saved["theme"]!.GetValue<string>());
        Assert.Equal("keep", saved["providers"]!["gateway"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_RenderBothModelChoices_SaveAndResetFastModel_AndApplyPrimaryToNewSessions()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-model-ui-").FullName;
        var variables = new[] { "TKCODE_HOME", "TKCODE_FAKE_PROVIDER", "TKCODE_EXTERNAL_RUNTIMES", "TKCODE_AGENTHOST_DIR" };
        var saved = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "model": "fake/fake-sonnet" }""");
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.LastModelId = "fake/fake-mini";
        state.Save();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            Program.ConfigureServices(services);
            await using var provider = services.BuildServiceProvider();
            try
            {
                await HeadlessApp.Session.Dispatch(async () =>
                {
                    var vm = provider.GetRequiredService<MainWindowViewModel>();
                    var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
                    window.Show();
                    await WaitUntil(() => vm.Conversation.Picker.Selected is not null);
                    Assert.Equal("fake/fake-sonnet", vm.Conversation.Picker.Selected!.Model.Ref);

                    await vm.OpenSettingsCommand.ExecuteAsync(null);
                    Assert.Equal("fake/fake-sonnet", vm.Settings.DefaultModels.Single(m => m.IsSelected).Value);
                    Assert.Equal("", vm.Settings.FastModels.Single(m => m.IsSelected).Value);
                    Assert.Equal(Strings.Get(TKCodeLangResourceKind.FollowSessionModel), vm.Settings.FastModelLabel);
                    Dispatcher.UIThread.RunJobs();
                    HeadlessApp.Save(window, "settings-models-follow-session");

                    vm.Settings.ChooseFastModelCommand.Execute(vm.Settings.FastModels.Single(m => m.Value == "fake/fake-mini"));
                    var settings = provider.GetRequiredService<UserSettingsService>();
                    Assert.Equal("fake/fake-mini", settings.Current.FastModel);
                    Assert.Equal("fake/fake-mini", vm.Settings.FastModels.Single(m => m.IsSelected).Value);
                    var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
                    var popup = view.FindControl<Popover>("FastModelPopup")!;
                    popup.ShouldUseOverlayLayer = true;
                    popup.IsOpen = true;
                    Dispatcher.UIThread.RunJobs();
                    HeadlessApp.Save(window, "settings-fast-model-popup");
                    popup.IsOpen = false;

                    vm.Settings.ChooseFastModelCommand.Execute(vm.Settings.FastModels.Single(m => m.Value == ""));
                    Assert.Null(settings.Current.FastModel);
                    Assert.Null(JsonNode.Parse(File.ReadAllText(settings.FilePath))!["fastModel"]);

                    // 主模型改动在空状态立即生效，新会话也不沿用临时选过的模型。
                    vm.Settings.ChooseDefaultModelCommand.Execute(vm.Settings.DefaultModels.Single(m => m.Value == "fake/fake-mini"));
                    Assert.Equal("fake/fake-mini", vm.Conversation.Picker.Selected!.Model.Ref);
                    vm.Conversation.Picker.SelectByRef("fake/fake-sonnet");
                    vm.Conversation.NewChat();
                    Assert.Equal("fake/fake-mini", vm.Conversation.Picker.Selected!.Model.Ref);

                    // 外部运行时可以用作主模型，快速模型只列 API 模型。
                    var external = new ModelDto("external", "External", "test-cli", "external", false, null,
                        ["Off"], "Off", [128000], false, "test-cli/external", "Test CLI", Runtime: "test-cli");
                    vm.Conversation.Picker.Load([.. vm.Conversation.Picker.AllModels, external]);
                    settings.SetFastModel("fake/fake-mini");
                    Assert.Contains(vm.Settings.DefaultModels, m => m.Value == external.Ref);
                    Assert.DoesNotContain(vm.Settings.FastModels, m => m.Value == external.Ref);
                    Dispatcher.UIThread.RunJobs();
                    HeadlessApp.Save(window, "settings-models-configured");
                    vm.CloseSettingsCommand.Execute(null);

                    // 标题事件通过真实 Host 的源生成 JSON-RPC 到达界面，侧栏与主会话一起更新。
                    const string firstMessage = "Please explain how primary and fast model settings work in a new conversation.";
                    vm.Conversation.ComposerText = firstMessage;
                    await vm.Conversation.SendCommand.ExecuteAsync(null);
                    var expectedTitle = firstMessage[..24];
                    await WaitUntil(() => vm.Conversation.Active is { IsRunning: false } live && live.Title == expectedTitle);
                    Assert.Equal(expectedTitle, vm.Conversation.Sessions.Single().DisplayTitle);
                    Assert.Equal("fake/fake-mini", vm.Conversation.Active!.ModelId);

                    var item = vm.Conversation.Sessions.Single();
                    var client = (IAgentHostClient)provider.GetRequiredService<AgentHostConnection>();
                    await vm.Conversation.RenameSessionAsync(item, "Manual choice");
                    client.OnSessionEvent(new SessionEventNotification(item.SessionId, 1000, new SessionTitleChanged("Delayed generated title")));
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("Manual choice", vm.Conversation.Active.Title);
                    await vm.Conversation.RenameSessionAsync(item, "");
                    var resetTitle = vm.Conversation.Active.Title;
                    client.OnSessionEvent(new SessionEventNotification(item.SessionId, 1001, new SessionTitleChanged("Another delayed title")));
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(resetTitle, vm.Conversation.Active.Title);
                    window.Close();
                    return true;
                }, TestContext.Current.CancellationToken);
            }
            finally
            {
                provider.GetRequiredService<AgentHostConnection>().Dispose();
            }
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public void GeneratedTitleEvent_UpdatesTheLiveSession()
    {
        var session = new SessionViewModel(new SessionDto("id", "/tmp", "fake/fake-mini", "/tmp/session.jsonl", new SessionOptionsDto())) { Title = "Original message" };
        session.Apply(new SessionTitleChanged("模型配置"));
        Assert.Equal("模型配置", session.Title);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(predicate(), "Host did not load models in time.");
    }
}
