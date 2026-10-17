using System.Collections.Frozen;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Previews;

/// <summary>源码查看：任何不超过 <see cref="MaxBytes"/> 的文本文件，按扩展名 / 文件名选择 TextMate 语法着色。</summary>
public sealed class CodePreviewProvider : IFilePreviewProvider
{
    public const long MaxBytes = 2 * 1024 * 1024;

    // 没有扩展名或扩展名不能直接对应语法的常见文件
    private static readonly FrozenDictionary<string, string> FileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        ["Makefile"] = "makefile",
        ["GNUmakefile"] = "makefile",
        ["CMakeLists.txt"] = "cmake",
        [".gitignore"] = "ignore",
        [".dockerignore"] = "ignore",
        [".editorconfig"] = "ini",
        [".env"] = "shell",
        [".bashrc"] = "shell",
        [".zshrc"] = "shell",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".slnx"] = "xml",
        [".xlf"] = "xml",
        [".config"] = "xml",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public LucideKind Icon => LucideKind.Code;
    public TKCodeLangResourceKind Title => TKCodeLangResourceKind.PreviewSource;

    public bool CanPreview(FileProbe probe) => !probe.IsBinary && probe.Length <= MaxBytes;

    public async Task<FilePreviewViewModel> CreateAsync(FileProbe probe, CancellationToken cancellationToken) =>
        new CodePreviewViewModel(await probe.ReadTextAsync(cancellationToken), LanguageFor(probe.Name));

    /// <summary>高亮器使用的语言名：已知文件名 → 补充的扩展名映射 → 扩展名本身（高亮器自带别名表，也会按扩展名查找语法）。</summary>
    public static string LanguageFor(string fileName)
    {
        if (FileNames.TryGetValue(fileName, out var byName))
        {
            return byName;
        }
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return "shell";
        }
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Extensions.TryGetValue(ext, out var mapped) ? mapped : ext.TrimStart('.');
    }
}

/// <summary>源码视图：整段文本 + 语言名，着色与虚拟化由 <see cref="Controls.CodeView"/> 完成。</summary>
public sealed class CodePreviewViewModel(string text, string language) : FilePreviewViewModel
{
    public string Text { get; } = text;
    public string Language { get; } = language;
}

/// <summary>Markdown 渲染预览（StreamMark），相对图片以文件所在目录为基准。</summary>
public sealed class MarkdownPreviewProvider : IFilePreviewProvider
{
    public LucideKind Icon => LucideKind.BookOpen;
    public TKCodeLangResourceKind Title => TKCodeLangResourceKind.PreviewRendered;

    public bool CanPreview(FileProbe probe) =>
        probe.Extension is ".md" or ".mdx" or ".markdown" && !probe.IsBinary && probe.Length <= CodePreviewProvider.MaxBytes;

    public async Task<FilePreviewViewModel> CreateAsync(FileProbe probe, CancellationToken cancellationToken) =>
        new MarkdownPreviewViewModel(await probe.ReadTextAsync(cancellationToken), Path.GetDirectoryName(probe.Path));
}

public sealed class MarkdownPreviewViewModel(string markdown, string? directory) : FilePreviewViewModel
{
    public string Markdown { get; } = markdown;

    /// <summary>相对图片的基准目录。</summary>
    public string? Directory { get; } = directory;
}
