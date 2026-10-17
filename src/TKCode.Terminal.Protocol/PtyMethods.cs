namespace TKCode.Terminal.Protocol;

/// <summary>控制消息的方法名（04 §4）。</summary>
public static class PtyMethods
{
    public const string Hello = "hello";
    public const string Auth = "auth";
    public const string Cancel = "$/cancel";

    public const string HostConfigure = "host/configure";
    public const string HostInfo = "host/info";
    public const string HostQuitCheck = "host/quitCheck";
    public const string HostQuit = "host/quit";
    public const string HostSessionAdded = "host/sessionAdded";
    public const string HostSessionRemoved = "host/sessionRemoved";
    public const string HostGoodbye = "host/goodbye";
    public const string AuthMintToken = "auth/mintToken";
    public const string AuthRevoke = "auth/revoke";

    public const string SessionCreate = "session/create";
    public const string SessionList = "session/list";
    public const string SessionAttach = "session/attach";
    public const string SessionDetach = "session/detach";
    public const string SessionInput = "session/input";
    public const string SessionResize = "session/resize";
    public const string SessionFocus = "session/focus";
    public const string SessionSetColors = "session/setColors";
    public const string SessionReset = "session/reset";
    public const string SessionClear = "session/clear";
    public const string SessionSignal = "session/signal";
    public const string SessionKill = "session/kill";
    public const string SessionClose = "session/close";
    public const string SessionForeground = "session/foreground";
    public const string SessionSetLabel = "session/setLabel";
    public const string SessionSetPersist = "session/setPersist";
    public const string SessionShare = "session/share";
    public const string SessionRead = "session/read";

    // 会话事件（H→C 通知，帧头带 sessionId）
    public const string EventExited = "session/exited";
    public const string EventTitle = "session/title";
    public const string EventCwd = "session/cwd";
    public const string EventBell = "session/bell";
    public const string EventNotification = "session/notification";
    public const string EventProgress = "session/progress";
    public const string EventCommandStarted = "session/commandStarted";
    public const string EventCommandFinished = "session/commandFinished";
    public const string EventClipboardWrite = "session/clipboardWrite";
    public const string EventResync = "session/resync";
    public const string EventInputOverflow = "session/inputOverflow";

    public const string UiClipboardRead = "ui/clipboardRead";

    public const string PersistenceList = "persistence/list";
    public const string PersistenceSave = "persistence/save";
    public const string PersistenceDelete = "persistence/delete";
    public const string PersistenceClear = "persistence/clear";
}

/// <summary>错误码（04 §4.10）。</summary>
public static class PtyErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;

    public const int Unauthenticated = -33001;
    public const int Forbidden = -33002;
    public const int VersionMismatch = -33003;
    public const int ProtocolError = -33004;
    public const int SessionNotFound = -33010;
    public const int SessionExited = -33011;
    public const int AlreadyAttached = -33012;
    public const int NotAttached = -33013;
    public const int SpawnFailed = -33020;
    public const int ProfileNotFound = -33021;
    public const int CwdNotFound = -33022;
    public const int SnapshotFailed = -33030;
    public const int PersistenceDisabled = -33040;
    public const int ReviveNotFound = -33041;
    public const int ShellIntegrationUnavailable = -33050;
    public const int CommandBusy = -33051;
    public const int HostShuttingDown = -33060;
    public const int HostDraining = -33061;
    public const int ResourceLimit = -33062;
    public const int InputOverflow = -33063;
}

/// <summary>对端返回的错误，或本端处理请求时要返回给对端的错误。</summary>
public sealed class PtyHostException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
