using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Images;
using StreamMark.Highlighting.TextMate;
using StreamMark.Mermaid;
using StreamMark.RaTeX;
using TKCode.Config;

using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Controls;

/// <summary>
/// StreamMark 渲染扩展：RaTeX 公式、TextMate 代码高亮、Mermaid 图表，以及图片管线（磁盘缓存、SVG）。整个应用共用一份。
/// 本地图片只允许读取项目目录之下的文件（<see cref="SetAllowedFileRoots"/>）；相对路径的基准目录由会话视图 /
/// 文件预览通过 <see cref="MarkdownServices.ImageBaseUriProperty"/> 设置。
/// </summary>
public static class MarkdownSetup
{
    private static readonly Lazy<MarkdownServices> Shared = new(() =>
    {
        // 语法按需懒加载：第一次遇到某种语言时由后台着色任务加载（只加载当前主题那一份），代码先以无色显示、加载完再着色。
        // 不在启动时预热：预热 7 种语法（明暗两个主题各一份）会常驻约 80MB
        var highlighter = new TextMateSyntaxHighlighter();
        return new MarkdownServices
        {
            Math = RaTeXMathRenderer.Shared,
            Highlighter = highlighter,
            Diagrams = [new MermaidDiagramRenderer()],
            Images = new ImagePipeline(new ImagePipelineOptions
            {
                DiskCacheDirectory = Path.Combine(ConfigPaths.Home, "cache", "images"),
                SvgRasterizer = new SvgImageRasterizer(),
                DecodeDensity = s_originalImages ? null : CompressedDecodeDensity,
                // 缩小后的图片以 WebP 存进磁盘缓存，再次打开时不再解码原图
                ThumbnailEncoder = new WebPImageEncoder(),
            }),
            // 文本区右键菜单暂时关闭：StreamMark 创建的是 Avalonia 原生 MenuFlyout，其 MenuFlyoutPresenter
            // 在 AtomUI 主题下没有模板（AtomUI 只为自己的 MenuFlyoutPresenter 提供），弹出后是 0 尺寸的空菜单。
            // StreamMark 支持 AtomUI 菜单后改回 true；文字（每次打开时取当前界面语言）已经接好。
            ContextMenu = false,
            MenuText = command => Strings.Get(command switch
            {
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.Copy => TKCodeLangResourceKind.MarkdownMenuCopy,
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.CopyAsMarkdown => TKCodeLangResourceKind.MarkdownMenuCopyAsMarkdown,
                StreamMark.Avalonia.Selection.MarkdownMenuCommand.SelectAll => TKCodeLangResourceKind.MarkdownMenuSelectAll,
                _ => TKCodeLangResourceKind.MarkdownMenuFind,
            }),
            // 长代码块折叠后的展开 / 收起文字、diff 视图切换提示（每次渲染时取当前界面语言）
            CodeBlocks = CodeBlockOptions.Default with
            {
                ExpandText = lines => Strings.Format(TKCodeLangResourceKind.CodeExpandFormat, lines),
                CollapseText = () => Strings.Get(TKCodeLangResourceKind.CodeCollapse),
                // 参数是将要切换到的视图
                DiffModeText = mode => Strings.Get(mode switch
                {
                    StreamMark.Avalonia.Rendering.DiffViewMode.Split => TKCodeLangResourceKind.DiffSplitView,
                    StreamMark.Avalonia.Rendering.DiffViewMode.Unified => TKCodeLangResourceKind.DiffUnifiedView,
                    _ => TKCodeLangResourceKind.DiffInlineView,
                }),
            },
        };
    });

    public static MarkdownServices Services => Shared.Value;

    // 默认压缩：文档内图片按逻辑尺寸解码（每个逻辑像素 1 个物理像素），Retina 上内存约为原来的 1/4；点击查看时加载原图
    private const double CompressedDecodeDensity = 1;
    private static bool s_originalImages;

    /// <summary>Markdown 图片按原始清晰度显示（settings.json 的 originalImages）；修改后对之后排版的图片生效。</summary>
    public static bool OriginalImages
    {
        get => s_originalImages;
        set
        {
            s_originalImages = value;
            if (Shared.IsValueCreated)
            {
                Shared.Value.Images.DecodeDensity = value ? null : CompressedDecodeDensity;
            }
        }
    }

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
