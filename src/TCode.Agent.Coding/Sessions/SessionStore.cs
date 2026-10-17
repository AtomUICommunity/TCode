using System.Text;
using System.Text.Json;
using TCode.Agent.Coding.Prompt;
using TCode.Ai.Messages;
using TCode.Ai.Models;

namespace TCode.Agent.Coding.Sessions;

/// <summary>
/// 只追加的 JSONL 会话文件。文件在第一条消息写入时才创建；损坏的行在读取时跳过。
/// 默认位置：~/.tcode/sessions/&lt;cwd 编码&gt;/&lt;时间戳&gt;_&lt;sessionId&gt;.jsonl
/// </summary>
public sealed class SessionStore
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _leafId;
    private bool _created;
    /// <summary>文件创建前追加的非消息记录（模型 / 参数变化），随第一条消息一起写入，避免空会话落盘。</summary>
    private readonly StringBuilder _pending = new();

    private SessionStore(string filePath, string sessionId, string cwd)
    {
        FilePath = filePath;
        SessionId = sessionId;
        Cwd = cwd;
    }

    public string FilePath { get; }
    public string SessionId { get; }
    public string Cwd { get; }

    public static string DefaultRoot =>
        Path.Combine(TCodePaths.Home, "sessions");

    public static SessionStore Create(string cwd, string? root = null)
    {
        var sessionId = Guid.CreateVersion7().ToString("N");
        var dir = Path.Combine(root ?? DefaultRoot, EncodeCwd(cwd));
        var file = Path.Combine(dir, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{sessionId}.jsonl");
        return new SessionStore(file, sessionId, cwd);
    }

    /// <summary>在指定文件创建新会话（子 Agent 的转录不放在会话目录下，不出现在会话列表中）。</summary>
    public static SessionStore CreateAt(string filePath, string cwd) =>
        new(Path.GetFullPath(filePath), Guid.CreateVersion7().ToString("N"), cwd);

    /// <summary>打开既有会话，返回存储与沿当前分支（根 → 叶）的消息。</summary>
    public static (SessionStore Store, IReadOnlyList<Message> Messages) Open(string filePath)
    {
        var (store, snapshot) = OpenSnapshot(filePath);
        return (store, snapshot.Messages);
    }

    /// <summary>打开既有会话，同时返回完整快照（外部运行时需要其中的原生会话 id）。</summary>
    public static (SessionStore Store, SessionSnapshot Snapshot) OpenSnapshot(string filePath)
    {
        var snapshot = Read(filePath);
        return (new SessionStore(filePath, snapshot.SessionId, snapshot.Cwd) { _leafId = snapshot.LeafId, _created = true }, snapshot);
    }

    /// <summary>
    /// 只读解析会话文件：沿当前分支（根 → 叶）收集消息，以及分支上最后一次生效的模型与各项参数。
    /// 用于会话列表与恢复会话；不会创建 <see cref="SessionStore"/>。
    /// </summary>
    public static SessionSnapshot Read(string filePath)
    {
        SessionHeader? header = null;
        string? customTitle = null;
        var pinned = false;
        var entries = new Dictionary<string, TreeEntry>();
        string? leaf = null;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            SessionEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, SessionJsonContext.Default.SessionEntry);
            }
            catch (JsonException)
            {
                continue;
            }
            switch (entry)
            {
                case SessionHeader h:
                    header = h;
                    break;
                case SessionMetaEntry meta:
                    customTitle = meta.Title is null ? customTitle : meta.Title.Length == 0 ? null : meta.Title;
                    pinned = meta.Pinned ?? pinned;
                    break;
                case TreeEntry t:
                    entries[t.Id] = t;
                    leaf = t.Id;
                    break;
            }
        }
        if (header is null)
        {
            throw new InvalidDataException($"'{filePath}' is not a TCode session file.");
        }

        var branch = new List<TreeEntry>();
        for (var id = leaf; id is not null && entries.TryGetValue(id, out var entry); id = entry.ParentId)
        {
            branch.Add(entry);
        }
        branch.Reverse();

        var messages = new List<Message>();
        ModelChangeEntry? model = null;
        ThinkingLevel? thinking = null;
        bool? fast = null;
        int? contextWindow = null;
        RuntimeSessionEntry? runtime = null;
        TCode.Agent.PermissionMode? permission = null;
        string? role = null;
        IReadOnlyList<PromptSection>? initialPrompt = null;
        IReadOnlyList<PromptSection>? currentPrompt = null;
        IReadOnlyList<TCode.Agent.Coding.Tools.TaskItem>? tasks = null;
        foreach (var entry in branch)
        {
            switch (entry)
            {
                case TasksEntry t:
                    tasks = t.Tasks;
                    break;
                case SystemPromptEntry p:
                    initialPrompt ??= p.Sections;
                    currentPrompt = p.Sections;
                    break;
                case MessageEntry m:
                    messages.Add(m.Message);
                    break;
                case ModelChangeEntry c:
                    model = c;
                    break;
                case OptionsChangeEntry o:
                    thinking = o.ThinkingLevel ?? thinking;
                    fast = o.FastMode ?? fast;
                    contextWindow = o.ContextWindow ?? contextWindow;
                    permission = o.PermissionMode ?? permission;
                    role = o.Role ?? role;
                    break;
                case RuntimeSessionEntry r:
                    runtime = r;
                    break;
            }
        }
        return new SessionSnapshot(header.SessionId, header.Cwd, header.Timestamp, leaf, messages,
            model?.Provider, model?.ModelId, thinking, fast, contextWindow, runtime?.Runtime, runtime?.NativeSessionId, permission, customTitle, pinned)
        {
            InitialSystemPrompt = initialPrompt,
            SystemPrompt = currentPrompt,
            Tasks = tasks,
            Role = role,
        };
    }

    public Task AppendMessageAsync(Message message, CancellationToken ct = default)
        => AppendTreeAsync(parent => new MessageEntry(NewEntryId(), parent, ImageAttachments.ForStorage(message)), ct);

    public Task AppendModelChangeAsync(string provider, string modelId, CancellationToken ct = default)
        => AppendTreeAsync(parent => new ModelChangeEntry(NewEntryId(), parent, provider, modelId), ct);

    public Task AppendOptionsChangeAsync(ThinkingLevel? thinkingLevel, bool? fastMode, int? contextWindow, CancellationToken ct = default,
        TCode.Agent.PermissionMode? permissionMode = null, string? role = null)
        => AppendTreeAsync(parent => new OptionsChangeEntry(NewEntryId(), parent, thinkingLevel, fastMode, contextWindow, permissionMode, role), ct);

    public Task AppendSystemPromptAsync(IReadOnlyList<PromptSection> sections, CancellationToken ct = default)
        => AppendTreeAsync(parent => new SystemPromptEntry(NewEntryId(), parent, sections), ct);

    public Task AppendTasksAsync(IReadOnlyList<TCode.Agent.Coding.Tools.TaskItem> tasks, CancellationToken ct = default)
        => AppendTreeAsync(parent => new TasksEntry(NewEntryId(), parent, tasks), ct);

    public Task AppendRuntimeSessionAsync(string runtime, string nativeSessionId, CancellationToken ct = default)
        => AppendTreeAsync(parent => new RuntimeSessionEntry(NewEntryId(), parent, runtime, nativeSessionId), ct);

    /// <summary>追加元数据（重命名 / 置顶）。文件尚未创建时随第一条消息写入。</summary>
    public async Task AppendMetaAsync(string? title, bool? pinned, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var line = Serialize(new SessionMetaEntry(title, pinned));
            if (!_created)
            {
                _pending.AppendLine(line);
                return;
            }
            await File.AppendAllTextAsync(FilePath, line + Environment.NewLine, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>对未在 Host 中打开的会话文件直接追加元数据。</summary>
    public static Task AppendMetaToFileAsync(string filePath, string? title, bool? pinned, CancellationToken ct = default) =>
        File.AppendAllTextAsync(filePath, Serialize(new SessionMetaEntry(title, pinned)) + Environment.NewLine, ct);

    private async Task AppendTreeAsync(Func<string?, TreeEntry> factory, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entry = factory(_leafId);
            if (!_created && entry is not MessageEntry)
            {
                _pending.AppendLine(Serialize(entry));
                _leafId = entry.Id;
                return;
            }
            var builder = new StringBuilder();
            if (!_created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                builder.AppendLine(Serialize(new SessionHeader(SessionId, Cwd, CurrentVersion)));
                builder.Append(_pending);
            }
            builder.AppendLine(Serialize(entry));
            await File.AppendAllTextAsync(FilePath, builder.ToString(), ct);
            _created = true;
            _pending.Clear();
            _leafId = entry.Id;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string Serialize(SessionEntry entry) => JsonSerializer.Serialize(entry, SessionJsonContext.Default.SessionEntry);

    private static string NewEntryId() => Guid.NewGuid().ToString("N")[..8];

    private static string EncodeCwd(string cwd)
    {
        var chars = cwd.Select(c => c is '/' or '\\' or ':' ? '-' : c).ToArray();
        return "--" + new string(chars).Trim('-') + "--";
    }
}

/// <summary>会话文件的只读快照（当前分支）。</summary>
public sealed record SessionSnapshot(
    string SessionId,
    string Cwd,
    DateTimeOffset CreatedAt,
    string? LeafId,
    IReadOnlyList<Message> Messages,
    string? ModelProvider,
    string? ModelId,
    ThinkingLevel? ThinkingLevel,
    bool? FastMode,
    int? ContextWindow,
    string? Runtime = null,
    string? NativeSessionId = null,
    TCode.Agent.PermissionMode? PermissionMode = null,
    string? CustomTitle = null,
    bool IsPinned = false)
{
    /// <summary>内置 Agent 会话开头的系统提示（旧会话为 null）。</summary>
    public IReadOnlyList<PromptSection>? InitialSystemPrompt { get; init; }

    /// <summary>当前生效的系统提示（含中途变化）。</summary>
    public IReadOnlyList<PromptSection>? SystemPrompt { get; init; }

    /// <summary>会话的 role 名（null 表示 agent）。</summary>
    public string? Role { get; init; }

    /// <summary>任务清单的最新快照。</summary>
    public IReadOnlyList<TCode.Agent.Coding.Tools.TaskItem>? Tasks { get; init; }
}
