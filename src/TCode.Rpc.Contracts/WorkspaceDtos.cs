namespace TCode.Rpc;

/// <summary>输入框 @ 文件搜索（docs/design/composer.md §8）。</summary>
/// <param name="Cwd">搜索的目录；结果路径相对于它。</param>
/// <param name="Limit">最多返回的条目数（1–200）。</param>
public sealed record FileSearchRequest(string Cwd, string Query, int Limit);

/// <param name="Path">相对路径，用 / 分隔；目录以 / 结尾。</param>
/// <param name="Positions">路径中命中查询的字符下标。</param>
public sealed record FileSearchItemDto(string Path, bool IsDirectory, int Score, IReadOnlyList<int> Positions);

/// <summary>撤销 Agent 的一次文件改动（见 TCode.Agent.Coding.Tools.Files.FileRevert）。RestoreText 为 null 时删除文件（新建的文件）。</summary>
/// <param name="ExpectedSha256">改动后文件的 SHA-256；不一致说明文件已被改过，撤销被拒绝。</param>
public sealed record RevertFileRequest(string Path, string ExpectedSha256, string? RestoreText);

/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record RevertFileResult(string? Error);

/// <param name="Complete">索引是否完整（条目超过上限时为 false）。</param>
/// <param name="Indexing">索引正在建立或刷新。</param>
public sealed record FileSearchResultDto(IReadOnlyList<FileSearchItemDto> Items, bool Complete, bool Indexing);
