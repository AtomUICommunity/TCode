using Avalonia.Controls;
using Avalonia.Media;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Plugins;
using StreamMark.Core.Model;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 代码块头部右侧的“打开预览”：围栏信息里带了文件路径（<c>```md docs/a.md</c>、<c>```cs src/App.cs#L10</c>）
/// 且文件存在时，点击在工作区打开。提示文字在服务创建时取当前语言。
/// </summary>
internal sealed class OpenCodeFilePlugin : IMarkdownPlugin
{
    // 16×16 描边图标：箭头指向右上，表示在旁边打开
    private static readonly Geometry Icon = Geometry.Parse(
        "M8.2,2.2 L13.6,2.2 L13.6,7.6 M13.3,2.5 L6.8,9 M10.6,8 L10.6,13.6 L2.2,13.6 L2.2,5.2 L7,5.2");

    public string Name => "tkcode.openCodeFile";

    public void Configure(MarkdownPluginBuilder builder)
    {
        builder.AddBlockAction(new BlockActionDescriptor(
            "open-file",
            Strings.Get(TKCodeLangResourceKind.OpenFilePreview),
            Icon,
            static block => block is CodeBlock code && MarkdownLinks.FenceTarget(code.Info) is not null,
            OpenAsync));
    }

    private static Task OpenAsync(BlockActionContext context)
    {
        if (context.Block is not CodeBlock code || MarkdownLinks.FenceTarget(code.Info) is not { } target)
        {
            return Task.CompletedTask;
        }
        if (TopLevel.GetTopLevel(context.View) is not { DataContext: MainWindowViewModel vm })
        {
            return Task.CompletedTask;
        }
        if (MarkdownLinks.ResolveCodeSpan(target, MarkdownCodePaths.Directory(context.View, vm)) is { } path)
        {
            vm.Workbench.OpenFile(path);
        }
        return Task.CompletedTask;
    }
}
