using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using TCode.Desktop.Localization;
using TCode.Desktop.Updates;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Services;

/// <summary>
/// 系统托盘（Windows 通知区域 / macOS 菜单栏 / Linux StatusNotifier）与「关闭窗口不退出」：
/// 默认点窗口的关闭按钮只隐藏窗口，会话继续运行；从托盘菜单「退出 TCode」、macOS ⌘Q 或安装更新时才真正退出。
/// settings.json 的 closeToTray: false 恢复为关闭窗口即退出。
/// </summary>
public sealed class AppTray
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly UserSettingsService _settings;
    private readonly UpdatesViewModel _updates;
    private readonly MainWindowViewModel _main;
    private readonly TrayIcon _icon;

    public AppTray(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window window, UserSettingsService settings, UpdatesViewModel updates,
        MainWindowViewModel main)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        _updates = updates;
        _main = main;
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TCode/Assets/app-logo.png"))),
            ToolTipText = "TCode",
            Menu = new NativeMenu(),
        };
        _icon.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(app, [_icon]);

        window.Closing += OnWindowClosing;
        // macOS：窗口隐藏后点 Dock 图标重新显示
        if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    ShowWindow();
                }
            };
        }
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdatesViewModel.HasUpdate) or nameof(UpdatesViewModel.CardMode) or nameof(UpdatesViewModel.VersionText))
            {
                Rebuild();
            }
        };
        Strings.LanguageChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private bool CloseToTray => _settings.Current.CloseToTray != false;

    /// <summary>只拦截用户点关闭按钮（或 Alt+F4 / ⌘W）；退出程序、系统关机时照常关闭。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && CloseToTray)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    public void ShowWindow()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Show();
        _window.Activate();
    }

    private void Rebuild()
    {
        var menu = new NativeMenu();
        foreach (var item in BuildItems(_updates))
        {
            menu.Items.Add(item.Text is null ? new NativeMenuItemSeparator() : ToNative(item));
        }
        _icon.Menu = menu;
    }

    private NativeMenuItem ToNative(TrayItem item)
    {
        var native = new NativeMenuItem(item.Text!) { IsEnabled = item.Action != TrayAction.None };
        native.Click += (_, _) => Run(item.Action);
        return native;
    }

    private void Run(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Show:
                ShowWindow();
                break;
            case TrayAction.Download:
                ShowWindow();
                _updates.DownloadCommand.Execute(null);
                break;
            case TrayAction.Install:
                ShowWindow();
                _updates.InstallCommand.Execute(null);
                break;
            case TrayAction.CheckUpdates:
                ShowWindow();
                _main.OpenAboutAndCheckCommand.Execute(null);
                break;
            case TrayAction.Settings:
                ShowWindow();
                _main.OpenSettingsCommand.Execute(null);
                break;
            case TrayAction.Quit:
                _desktop.Shutdown();
                break;
        }
    }

    /// <summary>托盘菜单的内容（与平台无关，便于测试）；Text 为 null 表示分隔线。</summary>
    internal static IReadOnlyList<TrayItem> BuildItems(UpdatesViewModel updates)
    {
        var items = new List<TrayItem> { new(Strings.Get(TCodeLangResourceKind.TrayShow), TrayAction.Show), TrayItem.Separator };
        switch (updates.Service.Status)
        {
            case UpdateStatus.Available when updates.CanInstallInApp:
                items.Add(new(Strings.Format(TCodeLangResourceKind.TrayDownloadUpdateFormat, updates.VersionText), TrayAction.Download));
                break;
            case UpdateStatus.Available:
                items.Add(new(Strings.Format(TCodeLangResourceKind.UpdateAvailableFormat, updates.VersionText), TrayAction.Show));
                break;
            case UpdateStatus.Downloading:
                items.Add(new(Strings.Format(TCodeLangResourceKind.UpdateDownloadingFormat, updates.VersionText), TrayAction.None));
                break;
            case UpdateStatus.Ready:
                items.Add(new(Strings.Format(TCodeLangResourceKind.TrayInstallUpdateFormat, updates.VersionText), TrayAction.Install));
                break;
            default:
                items.Add(new(Strings.Get(TCodeLangResourceKind.UpdateCheckNow), TrayAction.CheckUpdates));
                break;
        }
        items.Add(new(Strings.Get(TCodeLangResourceKind.OpenSettings), TrayAction.Settings));
        items.Add(TrayItem.Separator);
        items.Add(new(Strings.Get(TCodeLangResourceKind.TrayQuit), TrayAction.Quit));
        return items;
    }
}

internal enum TrayAction
{
    None,
    Show,
    Download,
    Install,
    CheckUpdates,
    Settings,
    Quit,
}

internal sealed record TrayItem(string? Text, TrayAction Action)
{
    public static readonly TrayItem Separator = new(null, TrayAction.None);
}
