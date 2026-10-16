using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Images;
using StreamMark.Highlighting.TextMate;
using StreamMark.Mermaid;
using StreamMark.RaTeX;
using TCode.Config;

using TCode.Desktop.Localization;

namespace TCode.Desktop.Controls;

/// <summary>
/// StreamMark 渲染扩展：RaTeX 公式、TextMate 代码高亮、Mermaid 图表，以及图片管线（磁盘缓存、SVG）。整个应用共用一份。
/// 本地图片只允许读取项目目录之下的文件（<see cref="SetAllowedFileRoots"/>）；相对路径的基准目录由会话视图 /
/// 文件预览通过 <see cref="MarkdownServices.ImageBaseUriProperty"/> 设置。
/// </summary>
public static class MarkdownSetup
{
    private static readonly Lazy<MarkdownServices> Shared = new(() =>
    {
        var highlighter = new TextMateSyntaxHighlighter();
        // C# 等语法首次加载较慢，启动时在后台预热
        highlighter.Prewarm("csharp", "typescript", "python", "json", "bash", "rust", "go");
        return new MarkdownServices
        {
            Math = RaTeXMathRenderer.Shared,
            Highlighter = highlighter,
            Diagrams = [new MermaidDiagramRenderer()],
            Images = new ImagePipeline(new ImagePipelineOptions
            {
                DiskCacheDirectory = Path.Combine(ConfigPaths.Home, "cache", "images"),
                SvgRasterizer = new SvgImageRasterizer(),
            }),
            // 文本区右键菜单暂时关闭：StreamMark 创建的是 Avalonia 原生 MenuFlyout，其 MenuFlyoutPresenter
            // 在 AtomUI 主题下没有模板（AtomUI 只为自己的 MenuFlyoutPresenter 提供），弹出后是 0 尺寸的空菜单。
            // StreamMark 支持 AtomUI 菜单后改回 true；文字（每次打开时取当前界面语言）已经接好。
            ContextMenu = false,
            MenuText = command => Strings.Get(command switch
            {
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.Copy => TCodeLangResourceKind.MarkdownMenuCopy,
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.CopyAsMarkdown => TCodeLangResourceKind.MarkdownMenuCopyAsMarkdown,
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.SelectAll => TCodeLangResourceKind.MarkdownMenuSelectAll,
                _ => TCodeLangResourceKind.MarkdownMenuFind,
            }),
            // 长代码块折叠后的展开 / 收起文字、diff 视图切换提示（每次渲染时取当前界面语言）
            CodeBlocks = CodeBlockOptions.Default with
            {
                ExpandText = lines => Strings.Format(TCodeLangResourceKind.CodeExpandFormat, lines),
                CollapseText = () => Strings.Get(TCodeLangResourceKind.CodeCollapse),
                // 参数是将要切换到的视图
                DiffModeText = mode => Strings.Get(mode switch
                {
                    StreamMark.Avalonia.Rendering.DiffViewMode.Split => TCodeLangResourceKind.DiffSplitView,
                    StreamMark.Avalonia.Rendering.DiffViewMode.Unified => TCodeLangResourceKind.DiffUnifiedView,
                    _ => TCodeLangResourceKind.DiffInlineView,
                }),
            },
        };
    });

    public static MarkdownServices Services => Shared.Value;

    /// <summary>允许读取本地图片的目录（项目目录）。</summary>
    public static void SetAllowedFileRoots(IEnumerable<string> roots)
    {
        var images = Services.Images;
        var list = roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).ToArray();
        if (!images.Policy.AllowedFileRoots.SequenceEqual(list))
        {
            images.Policy = images.Policy with { AllowedFileRoots = list };
        }
    }
}
