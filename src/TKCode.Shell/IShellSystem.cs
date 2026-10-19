using System.Diagnostics;

namespace TKCode.Shell;

public enum ShellOs
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>探测所需的系统访问，可替换：测试里用内存里的假文件系统在任意平台上验证 Windows 的探测规则。</summary>
public interface IShellSystem
{
    ShellOs Os { get; }

    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    /// <summary>目录下子目录的完整路径；目录不存在或无法访问时为空。</summary>
    IReadOnlyList<string> GetDirectories(string path);

    /// <summary>可执行文件的文件版本（仅 Windows 有意义）；读不到时为 null，例如 Store 版的应用执行别名。</summary>
    Version? GetFileVersion(string path);

    /// <summary>文本文件的全部行；文件不存在时为空。</summary>
    IReadOnlyList<string> ReadAllLines(string path);
}

public sealed class RealShellSystem(Func<string, string?>? environment = null) : IShellSystem
{
    public static RealShellSystem Instance { get; } = new();

    public ShellOs Os => OperatingSystem.IsWindows() ? ShellOs.Windows : OperatingSystem.IsMacOS() ? ShellOs.MacOS : ShellOs.Linux;

    /// <param name="environment">替代进程环境的查询（pty host 用 Desktop 传来的基础环境）；为 null 时读进程环境。</param>
    public string? GetEnvironmentVariable(string name) =>
        environment is null ? Environment.GetEnvironmentVariable(name) : environment(name);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> GetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public Version? GetFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart > 0 ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ReadAllLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
