using System.Runtime.CompilerServices;
using Xunit;

namespace TCode.Terminal.Core.Tests.Replay;

/// <summary>回放夹具目录（11 §1.1.1）：读取输出目录中的副本，黄金样本写回源目录。</summary>
internal static class FixtureCatalog
{
    public static readonly bool UpdateGolden = Environment.GetEnvironmentVariable("TCODE_UPDATE_GOLDEN") == "1";

    public static string OutputRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string SourceRoot([CallerFilePath] string caller = "") => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(caller))!, "Fixtures");

    /// <summary>"source/name" 形式的夹具 id。</summary>
    public static TheoryData<string> Enumerate(params string[] sources)
    {
        var data = new TheoryData<string>();
        foreach (var source in sources)
        {
            var dir = Path.Combine(OutputRoot, source);
            foreach (var cast in Directory.EnumerateFiles(dir, "*.cast").Order(StringComparer.Ordinal))
            {
                data.Add($"{source}/{Path.GetFileNameWithoutExtension(cast)}");
            }
        }
        return data;
    }

    public static string Read(string id, string extension) => File.ReadAllText(Path.Combine(OutputRoot, id + extension));

    public static bool Exists(string id, string extension) => File.Exists(Path.Combine(OutputRoot, id + extension));

    /// <summary>known-differences.txt：每行 "source/name  # 原因"。</summary>
    public static IReadOnlyDictionary<string, string> KnownDifferences(string file)
    {
        var path = Path.Combine(OutputRoot, file);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var hash = line.IndexOf('#');
            var id = (hash < 0 ? line : line[..hash]).Trim();
            result[id] = hash < 0 ? string.Empty : line[(hash + 1)..].Trim();
        }
        return result;
    }

    /// <summary>失败时把实际结果写到 artifacts/test-results/grid/（CI 上传）。</summary>
    public static string WriteActual(string id, string extension, string content)
    {
        var dir = Path.Combine(SourceRoot(), "..", "..", "..", "artifacts", "test-results", "grid", Path.GetDirectoryName(id)!);
        Directory.CreateDirectory(dir);
        var path = Path.GetFullPath(Path.Combine(dir, Path.GetFileName(id) + ".actual" + extension));
        File.WriteAllText(path, content);
        return path;
    }
}
