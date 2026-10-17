using AtomUI;
using AtomUI.Localization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCode.Desktop.Services;

namespace TCode.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly UserSettingsService userSettings;
    private readonly AgentHostConnection host;
    private readonly ILogger<SettingsViewModel> settingsLogger;

    public MainWindowViewModel(
        SidebarViewModel sidebar,
        ConversationViewModel conversation,
        WorkbenchViewModel workbench,
        ThemeService themes,
        UserSettingsService userSettings,
        AgentHostConnection host,
        UpdatesViewModel updates,
        ILogger<SettingsViewModel> settingsLogger)
    {
        Updates = updates;
        // 卡片的「查看更新说明」打开设置 › 关于
        updates.OpenAbout = async () =>
        {
            IsSettingsOpen = true;
            await Settings.OpenAsync(SettingsSection.About);
        };
        Sidebar = sidebar;
        Conversation = conversation;
        Workbench = workbench;
        conversation.Files.OpenFiles = () => workbench.Tabs.OfType<FileTabViewModel>().Select(t => t.FilePath);
        Themes = themes;
        this.userSettings = userSettings;
        this.host = host;
        this.settingsLogger = settingsLogger;
        Explorer = new FileExplorerViewModel(workbench);
        // 打开文件或面板时自动显示编辑区
        workbench.OpenRequested += () => IsWorkbenchOpen = true;
        workbench.Emptied += () => IsWorkbenchOpen = false;
        // 变更面板与转录中的“审查 / 打开”跟随当前会话
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationViewModel.Active))
            {
                workbench.SetChangesSource(conversation.Active);
            }
        };
        // 文件树跟随当前会话的工作目录，没有会话时跟随所选项目
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConversationViewModel.Active) or nameof(ConversationViewModel.Workspace))
            {
                SyncExplorerRoot();
            }
        };
        SyncExplorerRoot();
    }

    public SidebarViewModel Sidebar { get; }
    public ConversationViewModel Conversation { get; }
    public WorkbenchViewModel Workbench { get; }
    public ThemeService Themes { get; }

    /// <summary>应用内更新：右下角提醒卡片、侧栏小圆点、设置 › 关于的「更新」一节。</summary>
    public UpdatesViewModel Updates { get; }

    /// <summary>右侧文件树。</summary>
    public FileExplorerViewModel Explorer { get; }

    [ObservableProperty]
    public partial bool IsExplorerOpen { get; set; }

    [RelayCommand]
    private void ToggleExplorer() => IsExplorerOpen = !IsExplorerOpen;

    private void SyncExplorerRoot() => Explorer.SetRoot(Conversation.Active?.Cwd ?? Conversation.Workspace?.Path);

    /// <summary>⌘K 命令面板。</summary>
    public CommandPaletteViewModel Palette => field ??= new CommandPaletteViewModel(this);

    /// <summary>设置页（全页，覆盖侧栏与主区域）。</summary>
    public SettingsViewModel Settings => field ??= new SettingsViewModel(userSettings, host, Conversation, Themes, SetLanguageAsync, settingsLogger);

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; private set; }

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await Settings.OpenAsync();
    }

    /// <summary>托盘菜单「检查更新」：打开设置 › 关于并立即检查。</summary>
    [RelayCommand]
    private async Task OpenAboutAndCheckAsync()
    {
        IsSettingsOpen = true;
        await Settings.OpenAsync(SettingsSection.About);
        await Updates.CheckNowCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    public bool IsChinese => Application.Current?.GetLanguageManager()?.Current.CurrentLanguage == LanguageTags.ZhCN;

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsWorkbenchOpen { get; set; }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ToggleWorkbench() => IsWorkbenchOpen = !IsWorkbenchOpen;

    [RelayCommand]
    private Task ToggleTheme() => Themes.ToggleAsync();

    /// <summary>在中文与英文之间切换；AtomUI 内置控件文案随之切换，选择保存到 settings.json。</summary>
    [RelayCommand]
    private Task ToggleLanguage() => SetLanguageAsync(IsChinese ? "en-US" : "zh-CN");

    private Task SetLanguageAsync(string language)
    {
        if (Application.Current?.GetLanguageManager() is { } manager && LanguageTag.TryParse(language, out var tag) &&
            (tag == LanguageTags.EnUS || tag == LanguageTags.ZhCN))
        {
            manager.ChangeLanguage(tag);
            userSettings.SetLanguage(tag.Value);
            OnPropertyChanged(nameof(IsChinese));
        }
        return Task.CompletedTask;
    }
}
