using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using TKCode.Terminal.Core;
using TKCode.Terminal.Host.Sessions;
using TKCode.Terminal.Protocol;
using TKCode.Terminal.Pty;

namespace TKCode.Terminal.Host;

public sealed partial class PtyHostServer
{
    private static PtyHostJsonContext J => PtyHostJsonContext.Default;

    /// <summary>只有完整令牌可以调用的方法（04 §4 权限列）。</summary>
    private static readonly HashSet<string> FullOnly =
    [
        PtyMethods.HostConfigure, PtyMethods.HostInfo, PtyMethods.HostQuitCheck, PtyMethods.HostQuit, PtyMethods.AuthMintToken, PtyMethods.AuthRevoke,
        PtyMethods.SessionSetColors, PtyMethods.SessionReset, PtyMethods.SessionClear, PtyMethods.SessionShare, PtyMethods.SessionSetLabel,
        PtyMethods.SessionSetPersist, PtyMethods.SessionResize, PtyMethods.SessionFocus,
        PtyMethods.PersistenceList, PtyMethods.PersistenceSave, PtyMethods.PersistenceDelete, PtyMethods.PersistenceClear,
    ];

    /// <summary>在读线程上调用：不阻塞。</summary>
    internal void Dispatch(ClientConnection c, uint sessionId, Envelope e)
    {
        var id = e.Id!.Value;
        var method = e.Method!;
        try
        {
            if (c.Scope == "none")
            {
                switch (method)
                {
                    case PtyMethods.Hello when !c.Hello:
                        Hello(c, id, e.Read(J.HelloParams));
                        return;
                    case PtyMethods.Auth when c.Hello:
                        Auth(c, id, e.Read(J.AuthParams));
                        return;
                    default:
                        c.Connection.RespondError(id, PtyErrors.Unauthenticated, "authenticate first");
                        c.Connection.Close(new ProtocolException($"{method} before authentication"));
                        return;
                }
            }
            if (!c.IsFull && FullOnly.Contains(method))
            {
                throw new PtyHostException(PtyErrors.Forbidden, $"{method} requires the full token");
            }
            Handle(c, id, sessionId, method, e);
        }
        catch (PtyHostException ex)
        {
            c.Connection.RespondError(id, ex.Code, ex.Message, sessionId);
        }
    }

    private void Handle(ClientConnection c, uint id, uint sessionId, string method, Envelope e)
    {
        switch (method)
        {
            case PtyMethods.HostConfigure:
                var settings = e.Read(J.HostSettingsDto);
                _context.Settings = settings;
                _context.CoreOptions = _context.CoreOptions with { ScrollbackMaxLines = Math.Max(settings.Persistence.ScrollbackLines, 10_000) };
                Configured?.Invoke(settings);
                c.Connection.Respond(id, new AppliedResult(true), J.AppliedResult);
                return;
            case PtyMethods.HostInfo:
                c.Connection.Respond(id, Info(), J.HostInfoResult);
                return;
            case PtyMethods.HostQuitCheck:
                Async(c, id, () => QuitCheckAsync(e.Read(J.QuitCheckParams)), J.QuitCheckResult);
                return;
            case PtyMethods.HostQuit:
                var quit = e.Read(J.QuitParams);
                Async(c, id, () => QuitAsync(quit, c), J.QuitResult);
                return;
            case PtyMethods.AuthMintToken:
                c.Connection.Respond(id, Mint(e.Read(J.MintTokenParams)), J.MintedToken);
                return;
            case PtyMethods.AuthRevoke:
                Revoke(e.Read(J.RevokeParams).TokenId);
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return;

            case PtyMethods.SessionCreate:
                var create = e.Read(J.SessionCreateParams);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (created, session) = await CreateSessionAsync(c, create).ConfigureAwait(false);
                        c.Connection.Respond(id, created, J.SessionCreatedDto);
                        if (created.Attach is not null && c.GetSubscription(created.SessionId) is { } sub)
                        {
                            c.Connection.AddProducer(sub);  // 应答已排队：快照块不会先于它
                        }
                        // 输出泵最后启动：会话可能在应答排队之前就已输出或退出，它的通知必须排在应答之后（客户端据此登记 handle）
                        session.StartPump();
                    }
                    catch (PtyHostException ex)
                    {
                        c.Connection.RespondError(id, ex.Code, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"conn{c.Id}: session/create failed: {ex}");
                        c.Connection.RespondError(id, PtyErrors.InternalError, ex.Message);
                    }
                });
                return;
            case PtyMethods.SessionList:
                c.Connection.Respond(id, _sessions.Values.Where(s => CanRead(c, s)).OrderBy(s => s.Id).Select(s => s.Info()).ToArray(), J.SessionInfoDtoArray);
                return;
            case PtyMethods.SessionAttach:
                var attach = e.Read(J.AttachParams);
                var attachSession = Find(c, Same(sessionId, attach.SessionId), write: false);
                _ = AttachAsync(c, attachSession, attach, respondTo: id).ContinueWith(t =>
                {
                    if (t.Exception?.InnerException is PtyHostException ex)
                    {
                        c.Connection.RespondError(id, ex.Code, ex.Message, attachSession.Id);
                    }
                }, TaskScheduler.Default);
                return;
            case PtyMethods.SessionDetach:
            {
                var sid = Same(sessionId, e.Read(J.SessionIdParams).SessionId);
                var sub = c.RemoveSubscription(sid) ?? throw new PtyHostException(PtyErrors.NotAttached, $"not attached to session {sid}");
                OnActor(c, id, sub.Session, () => { sub.Session.Detach(sub); return new EmptyDto(); }, J.EmptyDto);
                return;
            }
            case PtyMethods.SessionInput:
            {
                var p = e.Read(J.SessionInputParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                if (session.Exit is not null)
                {
                    throw new PtyHostException(PtyErrors.SessionExited, "session has exited");
                }
                var bytes = p.Bytes ?? Encoding.UTF8.GetBytes(p.Text ?? string.Empty);
                _ = session.Pty.WriteAsync(bytes);
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return;
            }
            case PtyMethods.SessionResize:
            {
                var p = e.Read(J.SessionResizeParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: false);
                var sub = c.GetSubscription(session.Id);
                OnActor(c, id, session, () =>
                {
                    session.Resize(sub, new TerminalSize(Math.Max(1, p.Cols), Math.Max(1, p.Rows), p.CellWidth, p.CellHeight), out var applied);
                    var size = session.Size;
                    return new ResizeResult(applied, new TerminalSizeDto(size.Columns, size.Rows, size.CellWidth, size.CellHeight), session.SizeOwner?.Client.Id);
                }, J.ResizeResult);
                return;
            }
            case PtyMethods.SessionFocus:
            {
                var session = Find(c, Same(sessionId, e.Read(J.SessionIdParams).SessionId), write: false);
                var sub = c.GetSubscription(session.Id) ?? throw new PtyHostException(PtyErrors.NotAttached, "not attached");
                OnActor(c, id, session, () => new FocusResult(session.Focus(sub)), J.FocusResult);
                return;
            }
            case PtyMethods.SessionSetColors:
            {
                var p = e.Read(J.SetColorsParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                OnActor(c, id, session, () => new SeqResult(session.SetColors(p.Palette)), J.SeqResult);
                return;
            }
            case PtyMethods.SessionReset:
            {
                var p = e.Read(J.ResetParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                OnActor(c, id, session, () => new SeqResult(session.Reset(p.Hard)), J.SeqResult);
                return;
            }
            case PtyMethods.SessionClear:
            {
                var p = e.Read(J.ClearParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                OnActor(c, id, session, () => new SeqResult(session.Clear(p.Screen, p.Scrollback, p.RedrawPrompt)), J.SeqResult);
                return;
            }
            case PtyMethods.SessionSignal:
            {
                var p = e.Read(J.SignalParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                Async(c, id, async () => new DeliveredResult(await SignalAsync(session, p).ConfigureAwait(false)), J.DeliveredResult);
                return;
            }
            case PtyMethods.SessionKill:
            {
                var session = Find(c, Same(sessionId, e.Read(J.SessionIdParams).SessionId), write: true);
                Async(c, id, async () =>
                {
                    await session.Pty.CloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    var exit = await session.Exited.ConfigureAwait(false);
                    return new KillResult(new ExitDto(exit.ExitCode, exit.Signal, exit.CoreDumped, exit.Message));
                }, J.KillResult);
                return;
            }
            case PtyMethods.SessionClose:
            {
                var p = e.Read(J.CloseParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                if (!c.IsFull && session.Create.Kind != "agent")
                {
                    throw new PtyHostException(PtyErrors.Forbidden, "agents can only close their own sessions");
                }
                Async(c, id, () => CloseSessionAsync(session, p.Force), J.CloseResult);
                return;
            }
            case PtyMethods.SessionForeground:
            {
                var session = Find(c, Same(sessionId, e.Read(J.SessionIdParams).SessionId), write: false);
                Async(c, id, () => ForegroundAsync(session), J.ForegroundDto);
                return;
            }
            case PtyMethods.SessionShare:
            {
                var p = e.Read(J.ShareParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: true);
                session.SharedWith = p.Access == "none" ? null : p.AgentSessionId;
                session.SharedAccess = p.Access switch { "write" => ShareAccess.Write, "read" => ShareAccess.Read, _ => ShareAccess.None };
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return;
            }
            case PtyMethods.SessionSetLabel:
            {
                var p = e.Read(J.SetLabelParams);
                Find(c, Same(sessionId, p.SessionId), write: true).Label = p.Label;
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return;
            }
            case PtyMethods.SessionSetPersist:
            {
                var p = e.Read(J.SetPersistParams);
                Find(c, Same(sessionId, p.SessionId), write: true).Persist = p.Persist;
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return;
            }
            case PtyMethods.SessionRead:
            {
                var p = e.Read(J.ReadParams);
                var session = Find(c, Same(sessionId, p.SessionId), write: false);
                OnActor(c, id, session, () => Read(session, p), J.ReadResult);
                return;
            }
            default:
                if (HandleExtension(c, id, sessionId, method, e))
                {
                    return;
                }
                throw new PtyHostException(PtyErrors.MethodNotFound, $"unknown method {method}");
        }
    }

    /// <summary>持久化等其余方法（PtyHostServer.Persistence.cs）。</summary>
    private partial bool HandleExtension(ClientConnection c, uint id, uint sessionId, string method, Envelope e);

    internal event Action<HostSettingsDto>? Configured;

    // ---- 握手（§2.4）----

    private void Hello(ClientConnection c, uint id, HelloParams p)
    {
        if (p.ClientNonce.Length != Handshake.NonceSize)
        {
            throw new PtyHostException(PtyErrors.InvalidParams, "clientNonce must be 32 bytes");
        }
        if (p.Protocol.Major != ProtocolVersion.Current.Major)
        {
            throw new PtyHostException(PtyErrors.VersionMismatch, $"host protocol {ProtocolVersion.Current.Major}, client {p.Protocol.Major}");
        }
        c.Kind = p.ClientKind;
        c.Pid = p.Pid;
        c.ForeignBuild = p.GhosttyBuildId is { } clientBuild && _options.GhosttyBuildId is { } hostBuild && clientBuild != hostBuild;
        c.ClientNonce = p.ClientNonce;
        c.ServerNonce = Handshake.NewNonce();
        var proofKey = _options.Token;
        if (p.TokenId is { } tokenId)
        {
            // 未知的 tokenId 也照常应答（用随机密钥），不泄露哪些 id 存在；认证必然失败
            proofKey = _tokens.TryGetValue(tokenId, out var entry) ? entry.Token : Handshake.NewToken();
            c.ClaimedTokenId = tokenId;
        }
        c.Connection.Respond(id, new HelloResult
        {
            Protocol = new ProtocolVersion(ProtocolVersion.Current.Major, Math.Min(ProtocolVersion.Current.Minor, p.Protocol.Minor)),
            HostVersion = _options.HostVersion,
            Build = _options.Build,
            GhosttyBuildId = _options.GhosttyBuildId,
            InstanceId = _options.InstanceId,
            Pid = Environment.ProcessId,
            ServerNonce = c.ServerNonce,
            ServerProof = Handshake.ServerProof(proofKey, p.ClientNonce, c.ServerNonce),
            JobBound = _options.JobBound,
            Capabilities = ["revive"],
            Draining = Draining || c.ForeignBuild,
        }, J.HelloResult);
    }

    private void Auth(ClientConnection c, uint id, AuthParams p)
    {
        string? scope = null;
        if (c.ClaimedTokenId is null)
        {
            if (Handshake.Verify(Handshake.ClientProof(_options.Token, c.ServerNonce!, c.ClientNonce!), p.ClientProof))
            {
                scope = "full";
            }
        }
        else if (_tokens.TryGetValue(c.ClaimedTokenId, out var entry) && (entry.Expires is not { } exp || exp > DateTimeOffset.UtcNow) &&
                 Handshake.Verify(Handshake.ClientProof(entry.Token, c.ServerNonce!, c.ClientNonce!), p.ClientProof))
        {
            scope = entry.Scope;
            c.TokenId = c.ClaimedTokenId;
        }
        if (scope is null)
        {
            Log.Warn($"conn{c.Id}: authentication failed (pid {c.Pid})");
            c.Connection.RespondError(id, PtyErrors.Unauthenticated, "authentication failed");
            c.Connection.Close(new UnauthorizedAccessException("authentication failed"));
            return;
        }
        c.Scope = scope;
        c.Connection.Authenticated = true;
        c.Connection.Respond(id, new AuthResult(scope, c.Id, _options.MaxSessions), J.AuthResult);
        Log.Info($"conn{c.Id}: {c.Kind} (pid {c.Pid}) authenticated as {scope}");
        ConnectionsChanged?.Invoke();
    }

    private MintedToken Mint(MintTokenParams p)
    {
        if (p.Scope != "agent")
        {
            throw new PtyHostException(PtyErrors.InvalidParams, "only agent tokens can be minted");
        }
        if (_tokens.Count >= 16)
        {
            throw new PtyHostException(PtyErrors.ResourceLimit, "too many tokens");
        }
        var token = Handshake.NewToken();
        var tokenId = Ulid.New();
        _tokens[tokenId] = new MintedTokenEntry(token, p.Scope, p.Label, p.TtlSeconds is { } ttl ? DateTimeOffset.UtcNow.AddSeconds(ttl) : null);
        return new MintedToken(token, tokenId);
    }

    private void Revoke(string tokenId)
    {
        _tokens.TryRemove(tokenId, out _);
        foreach (var c in _clients.Values.Where(c => c.TokenId == tokenId))
        {
            c.Connection.Close(new UnauthorizedAccessException("token revoked"));
        }
    }

    // ---- 会话 ----

    private async Task<(SessionCreatedDto Dto, Session Session)> CreateSessionAsync(ClientConnection c, SessionCreateParams p)
    {
        if (_stopping.IsCancellationRequested)
        {
            throw new PtyHostException(PtyErrors.HostShuttingDown, "host is shutting down");
        }
        if (c.ForeignBuild)
        {
            // 新构建的 Desktop 连上了旧 host：这个 host 从此只排空（§1.5），新会话建在当前构建的 host 里
            Draining = true;
        }
        if (Draining)
        {
            throw new PtyHostException(PtyErrors.HostDraining, "this host is draining; create sessions in the current build's host");
        }
        if (_sessions.Count >= _options.MaxSessions)
        {
            throw new PtyHostException(PtyErrors.ResourceLimit, $"session limit {_options.MaxSessions}");
        }
        if (!c.IsFull)
        {
            p = p with { Kind = "agent", Persist = p.Persist ?? false };  // agent 连接创建的一律是 agent 会话
        }
        var launch = SessionLauncher.Resolve(p, _context.Settings, out var actual);
        var size = launch.InitialSize;
        var persistentId = p.PersistentId ?? Ulid.New();
        var revive = p.ReviveFrom is { } reviveId ? LoadRevive(reviveId) : null;

        var coreOptions = _context.CoreOptions with
        {
            Size = size,
            Width = p.WidthMode == "grapheme" ? WidthPolicy.Grapheme : WidthPolicy.Compat,
            Replies = TerminalReplyProfile.Default with { AdvertiseOsc52 = _context.Settings.OscWrite != "deny", XtVersion = _context.Settings.XtVersion ?? "TKCode" },
        };
        ITerminalConnection pty;
        try
        {
            pty = LocalPty.Start(launch, _localPtyOptions);
        }
        catch (PtyException ex)
        {
            throw new PtyHostException(PtyErrors.SpawnFailed, $"{actual.File}: {ex.Message}");
        }
        var sessionId = Interlocked.Increment(ref _nextSession);
        Session? session = null;
        try
        {
            var effects = new DeferredEffects();
            RevivedDto? revived = null;
            TerminalCore core;
            if (revive is not null)
            {
                core = CreateRevivedCore(revive, coreOptions, effects, out var r);
                revived = r;
            }
            else
            {
                core = TerminalCore.Create(coreOptions, effects);
            }
            session = new Session(_context, sessionId, persistentId, pty, core, p, size) { Revived = revived };
            effects.Target = session;
            if (p.Palette is { } palette)
            {
                core.SetDefaultColors(Session.ToCorePalette(palette), palette.Scheme == "light" ? ColorScheme.Light : ColorScheme.Dark);
            }
            if (revive is not null)
            {
                await session.InvokeAsync(() => { ApplyRevive(session, p.ReviveBanner); return 0; }).ConfigureAwait(false);
            }
            _sessions[sessionId] = session;
            AttachResultDto? attach = null;
            if (p.Attach is { } a)
            {
                attach = await AttachAsync(c, session, a with { SessionId = sessionId }).ConfigureAwait(false);
            }
            BroadcastFull(PtyMethods.HostSessionAdded, session.Info(), J.SessionInfoDto, except: c);
            Log.Info($"session {sessionId} created ({actual.File}, pid {session.Pid}, {size.Columns}x{size.Rows})");
            var dto = new SessionCreatedDto
            {
                SessionId = sessionId,
                PersistentId = persistentId,
                Pid = session.Pid,
                Launch = actual,
                Revived = session.Revived,
                Attach = attach,
            };
            return (dto, session);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pty.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private LocalPtyOptions? _localPtyOptions;

    /// <summary>测试与发布布局：helper / conpty 所在目录（默认是 host 自己的目录）。</summary>
    public LocalPtyOptions LocalPtyOptions { set => _localPtyOptions = value; }

    /// <summary>
    /// 在 actor 上原子地取 S、编码快照、登记游标（§8.2）。<paramref name="respondTo"/> 不为空时在 actor 上立即应答，
    /// 然后才把订阅挂到写线程上：应答一定先于快照块。为空时（create 内的 attach）由调用方应答后再调 <see cref="Subscription"/> 的挂接。
    /// </summary>
    private Task<AttachResultDto> AttachAsync(ClientConnection c, Session session, AttachParams p, uint? respondTo = null)
    {
        var mode = p.Mode == "agent" || c.Kind == ClientKind.Agent ? SubscriptionMode.Agent : SubscriptionMode.View;
        if (c.ForeignBuild && p.Snapshot == "ghostty")
        {
            p = p with { Snapshot = "vt" };  // 二进制快照只在同一 libghostty 构建之间有效（01 §3.2 规则 3）
        }
        var subscription = new Subscription(session, c, mode, Interlocked.Increment(ref _nextAttach));
        if (!c.TryAddSubscription(subscription))
        {
            throw new PtyHostException(PtyErrors.AlreadyAttached, $"already attached to session {session.Id}");
        }
        return session.InvokeAsync(() =>
        {
            try
            {
                var result = session.Attach(subscription, p);
                if (respondTo is { } id)
                {
                    c.Connection.Respond(id, result, J.AttachResultDto, session.Id);
                    c.Connection.AddProducer(subscription);
                }
                return result;
            }
            catch
            {
                c.RemoveSubscription(session.Id);
                throw;
            }
        });
    }

    private async Task<CloseResult> CloseSessionAsync(Session session, bool force)
    {
        if (!force && session.Exit is null)
        {
            var fg = await ForegroundAsync(session).ConfigureAwait(false);
            if (fg.Running)
            {
                return new CloseResult(false, Running(session, fg));
            }
        }
        DeleteRevive(session.PersistentId);
        await RemoveSessionAsync(session, "closed").ConfigureAwait(false);
        return new CloseResult(true);
    }

    private static async Task<bool> SignalAsync(Session session, SignalParams p)
    {
        if (session.Exit is not null)
        {
            throw new PtyHostException(PtyErrors.SessionExited, "session has exited");
        }
        if (!Enum.TryParse<TerminalSignal>(p.Signal, ignoreCase: true, out var signal))
        {
            throw new PtyHostException(PtyErrors.InvalidParams, $"unknown signal {p.Signal}");
        }
        return await session.Pty.SignalAsync(signal).ConfigureAwait(false);
    }

    /// <summary>"有程序在运行"的判定（§9.1）：前台不是 shell、不在忽略列表；有 shell 集成且光标在提示符处时视为空闲。</summary>
    private async Task<ForegroundDto> ForegroundAsync(Session session)
    {
        if (session.Exit is not null)
        {
            return new ForegroundDto { Running = false, ShellPid = session.Pid, Reason = "exited" };
        }
        var atPrompt = await session.InvokeAsync(() => session.Core.Commands.Current is not null ? session.Core.CursorAtPrompt : (bool?)null).ConfigureAwait(false);
        ForegroundInfo? fg = null;
        try
        {
            fg = await session.Pty.GetForegroundAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"session {session.Id}: foreground query failed: {ex.Message}");
        }
        if (fg is null)
        {
            return new ForegroundDto { Running = false, AtPrompt = atPrompt, ShellPid = session.Pid, Reason = "unknown" };
        }
        var process = new ForegroundProcessDto(fg.Pid, fg.Name);
        if (fg.IsShell)
        {
            return new ForegroundDto { Running = false, AtPrompt = atPrompt, ShellPid = session.Pid, Reason = "idle" };
        }
        if (atPrompt == true)
        {
            return new ForegroundDto { Running = false, AtPrompt = true, ShellPid = session.Pid, Foreground = process, Reason = "atPrompt" };
        }
        if (fg.Name is { } name && _context.Settings.ConfirmIgnoreProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new ForegroundDto { Running = false, AtPrompt = atPrompt, ShellPid = session.Pid, Foreground = process, Reason = "idle" };
        }
        return new ForegroundDto { Running = true, AtPrompt = atPrompt, ShellPid = session.Pid, Foreground = process, Reason = "foregroundProcess" };
    }

    private static RunningSessionDto Running(Session session, ForegroundDto fg) => new()
    {
        SessionId = session.Id,
        PersistentId = session.PersistentId,
        Label = session.Label,
        Title = session.Title,
        Kind = session.Create.Kind,
        Foreground = fg.Foreground,
        AtPrompt = fg.AtPrompt,
    };

    private async Task<QuitCheckResult> QuitCheckAsync(QuitCheckParams p)
    {
        var running = new List<RunningSessionDto>();
        foreach (var s in _sessions.Values.OrderBy(s => s.Id))
        {
            if (!p.IncludeAgent && s.Create.Kind == "agent")
            {
                continue;
            }
            var fg = await ForegroundAsync(s).ConfigureAwait(false);
            if (fg.Running)
            {
                running.Add(Running(s, fg));
            }
        }
        return new QuitCheckResult([.. running]);
    }

    private static ReadResult Read(Session session, ReadParams p)
    {
        var text = p.Format == "vt" ? session.Core.FormatVt() : session.Core.FormatPlainText();
        if (p.Range == "screen" && p.Format != "vt")
        {
            var lines = text.Split('\n');
            text = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - session.Size.Rows)));
        }
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(text) > p.MaxBytes)
        {
            // 从头部截断，保留最新的内容（与 ACP terminal/output 一致）
            var chars = Math.Min(text.Length, p.MaxBytes);
            while (chars > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(text.Length - chars)) > p.MaxBytes)
            {
                chars--;
            }
            if (chars > 0 && chars < text.Length && char.IsLowSurrogate(text[text.Length - chars]))
            {
                chars--;
            }
            text = text[^chars..];
            truncated = true;
        }
        return new ReadResult(text, truncated);
    }

    private HostInfoResult Info() => new()
    {
        HostVersion = _options.HostVersion,
        UptimeSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
        Sessions = _sessions.Count,
        Connections = [.. _clients.Values.Where(c => c.Scope != "none").Select(c => new ConnectionInfoDto(c.Id, c.Kind, c.Pid, c.Scope))],
        ManagedMemoryBytes = GC.GetTotalMemory(false),
        Draining = Draining,
    };

    /// <summary>遗留 host（§1.5）：不接受新会话。</summary>
    public bool Draining { get; set; }

    // ---- 权限 ----

    private Session Find(ClientConnection c, uint sessionId, bool write)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new PtyHostException(PtyErrors.SessionNotFound, $"session {sessionId} not found");
        }
        if (!(write ? CanWrite(c, session) : CanRead(c, session)))
        {
            throw new PtyHostException(PtyErrors.Forbidden, $"no access to session {sessionId}");
        }
        return session;
    }

    /// <summary>受限令牌（§13.2、18.1 第 4 条）：只能访问 agent 会话与共享给它的会话。</summary>
    private bool CanRead(ClientConnection c, Session s) => c.IsFull || s.Create.Kind == "agent" || s.SharedAccess >= ShareAccess.Read;

    private bool CanWrite(ClientConnection c, Session s) => c.IsFull || s.Create.Kind == "agent" || s.SharedAccess >= ShareAccess.Write;

    private static uint Same(uint headerSessionId, uint paramSessionId)
    {
        if (headerSessionId != 0 && paramSessionId != 0 && headerSessionId != paramSessionId)
        {
            throw new PtyHostException(PtyErrors.InvalidParams, "sessionId in the frame header and params differ");
        }
        return paramSessionId != 0 ? paramSessionId : headerSessionId;
    }

    // ---- 应答辅助 ----

    private void Async<T>(ClientConnection c, uint id, Func<Task<T>> work, JsonTypeInfo<T> type) => _ = Task.Run(async () =>
    {
        try
        {
            c.Connection.Respond(id, await work().ConfigureAwait(false), type);
        }
        catch (PtyHostException ex)
        {
            c.Connection.RespondError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"conn{c.Id}: request {id} failed: {ex}");
            c.Connection.RespondError(id, PtyErrors.InternalError, ex.Message);
        }
    });

    private void OnActor<T>(ClientConnection c, uint id, Session session, Func<T> work, JsonTypeInfo<T> type) => session.Post(() =>
    {
        try
        {
            c.Connection.Respond(id, work(), type, session.Id);
        }
        catch (PtyHostException ex)
        {
            c.Connection.RespondError(id, ex.Code, ex.Message, session.Id);
        }
        catch (Exception ex)
        {
            Log.Error($"session {session.Id}: request {id} failed: {ex}");
            c.Connection.RespondError(id, PtyErrors.InternalError, ex.Message, session.Id);
        }
    });

    /// <summary>核心在会话对象之前创建：先把效果转发给占位对象，会话建好后再接上。</summary>
    private sealed class DeferredEffects : ITerminalEffects
    {
        public ITerminalEffects? Target { get; set; }

        public void WriteToPty(ReadOnlySpan<byte> reply) => Target?.WriteToPty(reply);
        public void Bell() => Target?.Bell();
        public void TitleChanged(string title) => Target?.TitleChanged(title);
        public void WorkingDirectoryChanged(string raw) => Target?.WorkingDirectoryChanged(raw);
        public ClipboardWriteResult ClipboardWrite(ClipboardTarget target, string mime, ReadOnlySpan<byte> data) =>
            Target?.ClipboardWrite(target, mime, data) ?? ClipboardWriteResult.Denied;
        public ClipboardReadDecision ClipboardRead(in PendingClipboardRead request) => Target?.ClipboardRead(request) ?? ClipboardReadDecision.Denied;
        public void Notification(string title, string body) => Target?.Notification(title, body);
        public void Progress(ProgressState state, int? percent) => Target?.Progress(state, percent);
        public void SemanticPrompt(in SemanticPromptEvent e) => Target?.SemanticPrompt(e);
        public void RenderHold(bool hold) => Target?.RenderHold(hold);
        public void FullReset() => Target?.FullReset();
        public void UnknownOsc(ReadOnlySpan<byte> payload, bool truncated) => Target?.UnknownOsc(payload, truncated);
    }
}
