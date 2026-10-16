using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;

namespace TCode.Desktop.Previews;

/// <summary>
/// 打开文件时的探测信息：扩展名、大小与开头若干字节。各预览器据此判断能否处理，正文按需再读。
/// </summary>
public sealed class FileProbe
{
    public const int HeadBytes = 8192;

    private FileProbe(string path, long length, byte[] head)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Length = length;
        Head = head;
        // 前 8KB 内出现 NUL 视为二进制
        IsBinary = head.AsSpan().IndexOf((byte)0) >= 0;
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>小写扩展名（含点），无扩展名时为空串。</summary>
    public string Extension { get; }

    public long Length { get; }
    public byte[] Head { get; }
    public bool IsBinary { get; }

    public static async Task<FileProbe> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var head = new byte[(int)Math.Min(stream.Length, HeadBytes)];
        await stream.ReadExactlyAsync(head, cancellationToken);
        return new FileProbe(path, stream.Length, head);
    }

    public Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken) => File.ReadAllBytesAsync(Path, cancellationToken);

    /// <summary>以 UTF-8 读取全文（统一为 \n 换行，去掉末尾空行）。</summary>
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(cancellationToken);
        return Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").TrimEnd('\n');
    }
}

/// <summary>
/// 一种文件查看方式（图片、Markdown 预览、源码……）。一个文件可匹配多个预览器，按注册顺序排列，
/// 第一个为默认视图，其余在工具栏中切换。新增文件类型只需实现本接口、注册，并为其视图模型提供 DataTemplate。
/// </summary>
public interface IFilePreviewProvider
{
    /// <summary>工具栏切换按钮的图标。</summary>
    LucideKind Icon { get; }

    /// <summary>工具栏切换按钮的提示文字。</summary>
    TCodeLangResourceKind Title { get; }

    bool CanPreview(FileProbe probe);

    /// <summary>加载内容（在后台执行 IO 与解码）。失败时抛出异常，由文件标签显示错误信息。</summary>
    Task<FilePreviewViewModel> CreateAsync(FileProbe probe, CancellationToken cancellationToken);
}

/// <summary>预览器生成的视图模型；视图由 DataTemplate 按具体类型选择。</summary>
public abstract class FilePreviewViewModel : ObservableObject, IDisposable
{
    public virtual void Dispose()
    {
    }
}

/// <summary>预览器注册表。顺序即优先级：前面的预览器成为默认视图。</summary>
public sealed class FilePreviewRegistry(IEnumerable<IFilePreviewProvider> providers)
{
    public static FilePreviewRegistry Default { get; } = new([
        new ImagePreviewProvider(),
        new MarkdownPreviewProvider(),
        new CodePreviewProvider(),
    ]);

    public IReadOnlyList<IFilePreviewProvider> Providers { get; } = providers.ToArray();

    public IReadOnlyList<IFilePreviewProvider> Match(FileProbe probe) => Providers.Where(p => p.CanPreview(probe)).ToArray();
}
