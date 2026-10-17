using System.Text.Json.Serialization;

namespace TCode.Terminal.Protocol;

// 控制消息的 DTO（04 §4）。全部登记在 PtyHostJsonContext；字段只增不减（次版本向后兼容，04 §1.5）。

public sealed record ProtocolVersion(int Major, int Minor)
{
    public static readonly ProtocolVersion Current = new(1, 0);
}

public enum ClientKind
{
    [JsonStringEnumMemberName("ui")] Ui,
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("test")] Test,
}

public sealed record HelloParams
{
    public required ProtocolVersion Protocol { get; init; }
    public required ClientKind ClientKind { get; init; }
    public string? ClientVersion { get; init; }
    public string? GhosttyBuildId { get; init; }
    public required byte[] ClientNonce { get; init; }
    public int Pid { get; init; }

    /// <summary>受限令牌的 id（不是秘密）：host 用对应的令牌计算 serverProof。完整令牌不填。</summary>
    public string? TokenId { get; init; }
}

public sealed record HelloResult
{
    public required ProtocolVersion Protocol { get; init; }
    public string? HostVersion { get; init; }
    public string? Build { get; init; }
    public string? GhosttyBuildId { get; init; }
    public required string InstanceId { get; init; }
    public int Pid { get; init; }
    public required byte[] ServerNonce { get; init; }
    public required byte[] ServerProof { get; init; }
    public bool JobBound { get; init; }
    public bool Draining { get; init; }
    public string[] Capabilities { get; init; } = [];
}

public sealed record AuthParams(byte[] ClientProof);

public sealed record AuthResult(string Scope, uint ConnectionId, int MaxSessions);

public sealed record HostSettingsDto
{
    /// <summary>OSC 52 写：allow | ask | deny。</summary>
    public string OscWrite { get; init; } = "allow";

    /// <summary>OSC 52 读：allow | ask | deny。</summary>
    public string OscRead { get; init; } = "ask";

    public int NotificationsPerSecond { get; init; } = 1;
    public string? XtVersion { get; init; }
    public string[] ConfirmIgnoreProcesses { get; init; } = [];
    public PersistenceSettingsDto Persistence { get; init; } = new();
    public int OutputRingBytes { get; init; } = 1 << 20;

    /// <summary>Desktop 启动时的原始环境（18.1 第 1 条）：新会话的环境以它为基础。</summary>
    public Dictionary<string, string>? BaseEnvironment { get; init; }
}

public sealed record PersistenceSettingsDto
{
    public bool Enabled { get; init; } = true;
    public int ScrollbackLines { get; init; } = 1000;
    public string OnQuit { get; init; } = "revive";
    public int OrphanGraceMinutes { get; init; } = 30;
    public int? KeepAliveMaxHours { get; init; }
    public int SaveIntervalSec { get; init; } = 30;
}

public sealed record AppliedResult(bool Applied);

public sealed record HostInfoResult
{
    public string? HostVersion { get; init; }
    public double UptimeSeconds { get; init; }
    public int Sessions { get; init; }
    public ConnectionInfoDto[] Connections { get; init; } = [];
    public long ManagedMemoryBytes { get; init; }
    public bool Draining { get; init; }
}

public sealed record ConnectionInfoDto(uint ConnectionId, ClientKind Kind, int Pid, string Scope);

public sealed record QuitCheckParams(bool IncludeAgent = false);

public sealed record QuitCheckResult(RunningSessionDto[] Running);

public sealed record RunningSessionDto
{
    public uint SessionId { get; init; }
    public string? PersistentId { get; init; }
    public string? Label { get; init; }
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public ForegroundProcessDto? Foreground { get; init; }
    public bool? AtPrompt { get; init; }
}

public sealed record ForegroundProcessDto(int? Pid, string? Name, string? CommandLine = null);

public sealed record QuitParams
{
    /// <summary>revive | keepAlive | close。</summary>
    public string Mode { get; init; } = "revive";

    /// <summary>user | shutdown | logoff | update | orphanTimeout。</summary>
    public string Reason { get; init; } = "user";

    public int? DeadlineMs { get; init; }
}

public sealed record QuitResult(int Saved, int Closed);

public sealed record SessionRemovedDto(uint SessionId, string? PersistentId, string Reason);

public sealed record GoodbyeDto(string Reason);

public sealed record MintTokenParams(string Scope, string? Label = null, int? TtlSeconds = null);

public sealed record MintedToken(byte[] Token, string TokenId);

public sealed record RevokeParams(string TokenId);

public sealed record TerminalSizeDto(int Cols, int Rows, int CellWidth = 0, int CellHeight = 0);

/// <summary>默认颜色：RGB 打包为 0xRRGGBB；Palette 为 256 项。</summary>
public sealed record PaletteDto(int Foreground, int Background, int? Cursor, int[] Palette, string Scheme = "dark");

public sealed record LaunchDto(string Command, string[] Args);

public sealed record SessionCreateParams
{
    public string? ProfileId { get; init; }
    public LaunchDto? Launch { get; init; }
    public string? Cwd { get; init; }
    public Dictionary<string, string?>? Env { get; init; }
    public required TerminalSizeDto Size { get; init; }
    public string WidthMode { get; init; } = "compat";
    public PaletteDto? Palette { get; init; }

    /// <summary>user | agent。</summary>
    public string Kind { get; init; } = "user";

    public string? AgentSessionId { get; init; }
    public string? Label { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
    public string ShellIntegration { get; init; } = "auto";
    public string? PersistentId { get; init; }
    public string? ReviveFrom { get; init; }
    public string? ReviveBanner { get; init; }
    public AttachParams? Attach { get; init; }
    public bool? Persist { get; init; }
}

public sealed record ActualLaunchDto(string File, string[] Args, string? Argv0, bool Login);

public sealed record RevivedDto(DateTimeOffset FromSavedAt, int Lines, bool UsedFallback);

public sealed record SessionCreatedDto
{
    public uint SessionId { get; init; }
    public required string PersistentId { get; init; }
    public int Pid { get; init; }
    public ActualLaunchDto? Launch { get; init; }
    public RevivedDto? Revived { get; init; }
    public AttachResultDto? Attach { get; init; }
}

public sealed record SessionInfoDto
{
    public uint SessionId { get; init; }
    public required string PersistentId { get; init; }
    public string Kind { get; init; } = "user";
    public string? Owner { get; init; }
    public string? AgentSessionId { get; init; }
    public string? Label { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public int Pid { get; init; }

    /// <summary>running | exited。</summary>
    public string State { get; init; } = "running";

    public ExitDto? Exit { get; init; }
    public TerminalSizeDto? Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastOutputAt { get; init; }
    public SubscriberCountDto Subscribers { get; init; } = new(0, 0);
    public Dictionary<string, string>? Tags { get; init; }
    public bool Revived { get; init; }
}

public sealed record SubscriberCountDto(int Ui, int Agent);

public sealed record ExitDto(int? ExitCode, string? Signal, bool CoreDumped = false, string? Message = null);

public sealed record SessionIdParams(uint SessionId);

public sealed record AttachParams
{
    public uint SessionId { get; init; }

    /// <summary>view | agent。</summary>
    public string Mode { get; init; } = "view";

    /// <summary>ghostty | vt | none。</summary>
    public string Snapshot { get; init; } = "ghostty";

    public ulong? ResumeFrom { get; init; }
    public bool ClaimSize { get; init; }
    public int? HistoryLines { get; init; }
}

public sealed record CoreOptionsDto
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public int CellWidth { get; init; }
    public int CellHeight { get; init; }
    public long ScrollbackMaxBytes { get; init; }
    public int? ScrollbackMaxLines { get; init; }
    public string WidthMode { get; init; } = "compat";
    public PaletteDto? Palette { get; init; }
}

public sealed record AttachMetadataDto
{
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public int Pid { get; init; }
    public string State { get; init; } = "running";
    public ExitDto? Exit { get; init; }
    public bool BellPending { get; init; }
    public NotificationEventDto[] PendingNotifications { get; init; } = [];
    public ProgressEventDto? Progress { get; init; }
}

public sealed record AttachResultDto
{
    public uint AttachId { get; init; }
    public ulong SnapshotSeq { get; init; }

    /// <summary>ghostty | vt | none | resume。</summary>
    public string SnapshotFormat { get; init; } = "ghostty";

    public long SnapshotLength { get; init; }
    public required CoreOptionsDto CoreOptions { get; init; }
    public required AttachMetadataDto Metadata { get; init; }
    public bool IsSizeOwner { get; init; }
}

public sealed record SessionInputParams(uint SessionId, string? Text = null, byte[]? Bytes = null);

public sealed record SessionResizeParams(uint SessionId, int Cols, int Rows, int CellWidth = 0, int CellHeight = 0);

public sealed record ResizeResult(bool Applied, TerminalSizeDto Size, uint? Owner);

public sealed record FocusResult(bool IsSizeOwner);

public sealed record SetColorsParams(uint SessionId, PaletteDto Palette);

public sealed record ResetParams(uint SessionId, bool Hard);

public sealed record ClearParams(uint SessionId, bool Scrollback, bool Screen, bool RedrawPrompt = false);

public sealed record SeqResult(ulong Seq);

public sealed record SignalParams(uint SessionId, string Signal, string Target = "foreground");

public sealed record DeliveredResult(bool Delivered);

public sealed record KillResult(ExitDto? Exit);

public sealed record CloseParams(uint SessionId, bool Force);

public sealed record CloseResult(bool Closed, RunningSessionDto? Confirm = null);

public sealed record ForegroundDto
{
    public bool Running { get; init; }
    public bool? AtPrompt { get; init; }
    public int ShellPid { get; init; }
    public ForegroundProcessDto? Foreground { get; init; }

    /// <summary>idle | atPrompt | foregroundProcess | childProcess | exited | unknown。</summary>
    public string Reason { get; init; } = "unknown";
}

public sealed record ShareParams(uint SessionId, string AgentSessionId, string Access);

public sealed record SetLabelParams(uint SessionId, string? Label);

public sealed record SetPersistParams(uint SessionId, bool Persist);

public sealed record ReadParams
{
    public uint SessionId { get; init; }

    /// <summary>screen | scrollback | all。</summary>
    public string Range { get; init; } = "screen";

    /// <summary>plain | vt | html。</summary>
    public string Format { get; init; } = "plain";

    public int MaxBytes { get; init; } = 256 * 1024;
}

public sealed record ReadResult(string Text, bool Truncated);

// ---- 事件 ----

public sealed record ExitedEventDto(ulong Seq, int? ExitCode, string? Signal, bool CoreDumped, bool Early, string? Message);

public sealed record TitleEventDto(ulong Seq, string Title);

public sealed record CwdEventDto(ulong Seq, string Uri, string? Path, bool Verified);

public sealed record SeqEventDto(ulong Seq);

public sealed record NotificationEventDto(ulong Seq, string? Title, string Body);

public sealed record ProgressEventDto(ulong Seq, string State, int? Percent);

public sealed record CommandStartedEventDto(ulong Seq, long CommandId, string? CommandLine);

public sealed record CommandFinishedEventDto(ulong Seq, long CommandId, int? ExitCode, long DurationMs);

public sealed record ClipboardWriteEventDto(ulong Seq, string Target, byte[] Data);

public sealed record ResyncEventDto(string Reason);

public sealed record InputOverflowEventDto(long DroppedBytes);

public sealed record ClipboardReadRequest(uint SessionId, string Target);

public sealed record ClipboardReadResponse(bool Allow, byte[]? Data = null);

// ---- 持久化 ----

public sealed record ReviveEntryDto
{
    public required string PersistentId { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string? Reason { get; init; }
    public string? Label { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? ProfileId { get; init; }
    public string Kind { get; init; } = "user";
    public int Lines { get; init; }
    public bool Compatible { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}

public sealed record PersistenceSaveParams(uint[]? SessionIds = null, string Reason = "manual");

public sealed record SaveFailureDto(uint SessionId, string Error);

public sealed record PersistenceSaveResult(int Saved, SaveFailureDto[] Failed);

public sealed record PersistenceDeleteParams(string[] PersistentIds);

public sealed record DeletedResult(int Deleted);

// ---- 发现（04 §1.4）----

public sealed record PtyHostRegistration
{
    public int FormatVersion { get; init; } = 1;
    public int Pid { get; init; }
    public DateTimeOffset? ProcessStartTime { get; init; }
    public required string InstanceId { get; init; }
    public required string Endpoint { get; init; }
    public required string Token { get; init; }
    public required ProtocolVersion Protocol { get; init; }
    public string? HostVersion { get; init; }
    public required string Build { get; init; }
    public string? GhosttyBuildId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public bool JobBound { get; init; }
}

public sealed record EmptyDto;
