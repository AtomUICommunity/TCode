using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;

namespace TKCode.Desktop.Services;

/// <summary>
/// 各会话输入框草稿的存放处（docs/design/composer-drafts.md §4）。键是会话 ID，空状态（新对话）是 <see cref="NewChatKey"/>。
/// 草稿同时落盘（~/.tkcode/drafts/&lt;键&gt;.json），重启应用后恢复；最多保留 <see cref="MaxAge"/>。
/// 内存里的字典只在 UI 线程上读写；磁盘写入排在一个后台队列里依次执行，写入失败时静默放弃（草稿是尽力而为的）。
/// </summary>
public sealed class DraftStore
{
    /// <summary>“新对话”空状态的草稿，整个应用一份。</summary>
    public const string NewChatKey = "new";

    /// <summary>草稿最多保留的时间，从最后一次编辑算起。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private const int MaxKeyLength = 64;

    private readonly Dictionary<string, ComposerDraft> _drafts = new(StringComparer.Ordinal);
    // 磁盘上有文件的键（读取完成后才准确）；已知没有文件时删除可以省掉
    private readonly HashSet<string> _persisted = new(StringComparer.Ordinal);
    // 读取之前就已经 Set / Remove 过的键：读取结果不能覆盖它们（否则会让刚删除的草稿复活）
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private Task _writes = Task.CompletedTask;
    private bool _loaded;

    /// <summary>落盘到 ~/.tkcode/drafts。</summary>
    public DraftStore()
        : this(Path.Combine(ConfigPaths.Home, "drafts"))
    {
    }

    /// <param name="directory">草稿目录；null 为只在内存里保存。</param>
    /// <param name="time">指定时钟（测试用）。</param>
    public DraftStore(string? directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>取出草稿；没有、或已超过 <see cref="MaxAge"/>（此时一并删除）时为 null。</summary>
    public ComposerDraft? Get(string key)
    {
        if (!_drafts.TryGetValue(key, out var draft))
        {
            return null;
        }
        if (Now - draft.EditedAt > MaxAge)
        {
            Remove(key);
            return null;
        }
        return draft;
    }

    /// <summary>保存草稿；空草稿等于删除。</summary>
    public void Set(string key, ComposerDraft draft)
    {
        if (draft.IsEmpty)
        {
            Remove(key);
            return;
        }
        _touched.Add(key);
        _drafts[key] = draft;
        if (PathOf(key) is { } path)
        {
            _persisted.Add(key);
            var file = ToFile(draft);
            Enqueue(() => Write(path, file));
        }
    }

    /// <summary>丢掉草稿（会话被删除、或草稿已经空了）。</summary>
    public void Remove(string key)
    {
        _touched.Add(key);
        _drafts.Remove(key);
        if (PathOf(key) is { } path && (!_loaded || _persisted.Contains(key)))
        {
            _persisted.Remove(key);
            Enqueue(() => Delete(path));
        }
    }

    /// <summary>等所有已排队的磁盘写入完成。</summary>
    public Task FlushAsync() => _writes;

    /// <summary>退出前同步等待磁盘写入完成（最多 2 秒）。</summary>
    public void Flush()
    {
        try
        {
            _writes.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>
    /// 启动后读入磁盘上的草稿，同时清理超过 <see cref="MaxAge"/> 的文件。读取期间已经 Set / Remove 过的键以内存为准。
    /// 返回读到的草稿数。
    /// </summary>
    public async Task<int> LoadAsync()
    {
        if (_directory is not { } directory)
        {
            _loaded = true;
            return 0;
        }
        var now = Now;
        var found = await Task.Run(() => ReadAll(directory, now));
        var count = 0;
        foreach (var (key, file) in found)
        {
            if (_touched.Contains(key))
            {
                continue;
            }
            var draft = FromFile(file, File.Exists);
            if (draft.IsEmpty)
            {
                // 图片都不在了、文字也没有：留着没有意义
                if (PathOf(key) is { } stale)
                {
                    Enqueue(() => Delete(stale));
                }
                continue;
            }
            _drafts[key] = draft;
            _persisted.Add(key);
            count++;
        }
        _loaded = true;
        return count;
    }

    private static List<(string Key, DraftFile File)> ReadAll(string directory, DateTimeOffset now)
    {
        var result = new List<(string, DraftFile)>();
        if (!Directory.Exists(directory))
        {
            return result;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (!IsValidKey(key))
            {
                continue;
            }
            try
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(path), DraftJsonContext.Default.DraftFile);
                if (file is null || now - file.UpdatedAt > MaxAge)
                {
                    Delete(path);
                    continue;
                }
                result.Add((key, file));
            }
            catch (JsonException)
            {
                // 损坏的文件：过期了就清掉，没过期留着（下一次保存会覆盖）
                if (now - File.GetLastWriteTimeUtc(path) > MaxAge)
                {
                    Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
        return result;
    }

    private string? PathOf(string key) =>
        _directory is null || !IsValidKey(key) ? null : Path.Combine(_directory, key + ".json");

    /// <summary>键直接用作文件名：只接受字母、数字、- 和 _（会话 ID 是 32 位十六进制）。</summary>
    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void Enqueue(Action action)
    {
        _writes = _writes.ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Write(string path, DraftFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 先写临时文件再改名：中途退出不会留下半个文件
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, DraftJsonContext.Default.DraftFile));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static DraftFile ToFile(ComposerDraft draft) => new()
    {
        UpdatedAt = draft.EditedAt,
        Segments = [.. ComposerSegments.ToDtoList(draft.Segments)],
        Images = [.. draft.Images.Select(i => new DraftImage(i.Path, i.MimeType, i.Number))],
        NextImageNumber = draft.NextImageNumber,
        McpPrompt = draft.McpPrompt is { } mcp
            ? new DraftMcpPrompt { Server = mcp.Server, Name = mcp.Name, Arguments = new Dictionary<string, string>(mcp.Arguments) }
            : null,
    };

    /// <summary>
    /// 磁盘上的草稿还原：图片文件已经不存在的，丢掉附件并删掉文字里对应的 Image tag；
    /// Image tag 的显示文字按当前语言重新生成（存下来的是写入时的语言）。
    /// </summary>
    internal static ComposerDraft FromFile(DraftFile file, Func<string, bool> exists)
    {
        var images = new List<ImageAttachmentViewModel>();
        var byNumber = new Dictionary<string, ImageAttachmentViewModel>(StringComparer.Ordinal);
        foreach (var image in file.Images.Where(i => exists(i.Path)))
        {
            var attachment = new ImageAttachmentViewModel(image.Path, image.MimeType, image.Number);
            if (byNumber.TryAdd(image.Number.ToString(CultureInfo.InvariantCulture), attachment))
            {
                images.Add(attachment);
            }
        }
        var segments = new List<Segment>();
        foreach (var segment in ComposerSegments.FromDto(file.Segments))
        {
            if (segment is TagSegment { Kind: TagKind.Image } tag)
            {
                if (byNumber.TryGetValue(tag.Value, out var attachment))
                {
                    segments.Add(attachment.Tag);
                }
                continue;
            }
            segments.Add(segment);
        }
        var next = Math.Max(file.NextImageNumber, images.Count == 0 ? 1 : images.Max(i => i.Number) + 1);
        var mcp = file.McpPrompt is { } p
            ? new McpPromptSnapshot(p.Server, p.Name, null, p.Arguments ?? new Dictionary<string, string>())
            : null;
        return new ComposerDraft(segments, images, next, mcp, file.UpdatedAt);
    }
}

/// <summary>草稿文件（~/.tkcode/drafts/&lt;键&gt;.json）。文字与 tag 复用 <see cref="PromptSegmentDto"/>。</summary>
internal sealed class DraftFile
{
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PromptSegmentDto> Segments { get; set; } = [];
    public List<DraftImage> Images { get; set; } = [];
    public int NextImageNumber { get; set; } = 1;
    public DraftMcpPrompt? McpPrompt { get; set; }
}

internal sealed record DraftImage(string Path, string MimeType, int Number);

internal sealed class DraftMcpPrompt
{
    public string Server { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string>? Arguments { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DraftFile))]
internal sealed partial class DraftJsonContext : JsonSerializerContext;
