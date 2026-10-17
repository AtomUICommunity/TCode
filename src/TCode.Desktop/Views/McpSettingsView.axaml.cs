using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TCode.Desktop.ViewModels;

namespace TCode.Desktop.Views;

public partial class McpSettingsView : UserControl
{
    public McpSettingsView()
    {
        InitializeComponent();
    }

    private McpSettingsViewModel? Vm => DataContext as McpSettingsViewModel;

    /// <summary>导入：选择一个配置文件（JSON / TOML），内容放入粘贴框并解析。</summary>
    private async void OnOpenImportFile(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Import is not { } import || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MCP config") { Patterns = ["*.json", "*.jsonc", "*.toml"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            import.LoadFile(path, await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            import.LoadFile(path, "");
        }
    }

    private async void OnCopyExport(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Export is { } export && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(export.Json);
        }
    }

    private async void OnSaveExport(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Export is not { } export || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "mcp.json",
            DefaultExtension = "json",
        });
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(export.Json);
    }
}
