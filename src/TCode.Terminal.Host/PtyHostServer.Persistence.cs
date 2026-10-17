using System.Diagnostics;
using System.Text;
using TCode.Terminal.Core;
using TCode.Terminal.Host.Sessions;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

/// <summary>持久化与 revive（04 §10）、退出模式（§10.6、§12）。</summary>
public sealed partial class PtyHostServer
{
    private PersistenceStore? _store;
    private long _lastPeriodicSave = Stopwatch.GetTimestamp();
    private int _saving;

    private void InitializePersistence()
    {
        if (_options.Home is { } home)
        {
            _store = new PersistenceStore(Path.Combine(home, "terminal", "revive"), _options.GhosttyBuildId ?? "unknown", _options.HostVersion);
            try
            {
                _store.RemoveIncomplete();
            }
            catch (IOException e)
            {
                Log.Warn($"revive cleanup failed: {e.Message}");
            }
            Ticked += PeriodicSave;
        }
    }

    private bool PersistenceEnabled => _store is not null && _context.Settings.Persistence.Enabled;

    private partial bool HandleExtension(ClientConnection c, uint id, uint sessionId, string method, Envelope e)
    {
        switch (method)
        {
            case PtyMethods.PersistenceList:
                c.Connection.Respond(id, _store?.List() ?? [], J.ReviveEntryDtoArray);
                return true;
            case PtyMethods.PersistenceSave:
            {
                var p = e.Read(J.PersistenceSaveParams);
                if (!PersistenceEnabled)
                {
                    throw new PtyHostException(PtyErrors.PersistenceDisabled, "persistence is disabled");
                }
                var targets = p.SessionIds is { } ids ? ids.Select(i => _sessions.GetValueOrDefault(i)).OfType<Session>() : _sessions.Values;
                Async(c, id, () => SaveSessionsAsync(targets.ToList(), p.Reason, force: true), J.PersistenceSaveResult);
                return true;
            }
            case PtyMethods.PersistenceDelete:
            {
                var p = e.Read(J.PersistenceDeleteParams);
                var deleted = p.PersistentIds.Count(pid => _store?.Delete(pid) == true);
                c.Connection.Respond(id, new DeletedResult(deleted), J.DeletedResult);
                return true;
            }
            case PtyMethods.PersistenceClear:
                _store?.Clear();
                c.Connection.Respond(id, new EmptyDto(), J.EmptyDto);
                return true;
            default:
                return false;
        }
    }

    // ---- 保存 ----

    private async Task<PersistenceSaveResult> SaveSessionsAsync(IReadOnlyList<Session> sessions, string reason, bool force, TimeSpan? deadline = null)
    {
        var saved = 0;
        var failed = new List<SaveFailureDto>();
        using var timeout = deadline is { } d ? new CancellationTokenSource(d) : new CancellationTokenSource();
        foreach (var s in sessions)
        {
            if (timeout.IsCancellationRequested)
            {
                failed.Add(new SaveFailureDto(s.Id, "deadline"));  // 超时的会话保留上一次的保存结果
                continue;
            }
            try
            {
                if (await SaveAsync(s, reason, force).WaitAsync(timeout.Token).ConfigureAwait(false))
                {
                    saved++;
                }
            }
            catch (OperationCanceledException)
            {
                failed.Add(new SaveFailureDto(s.Id, "deadline"));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warn($"session {s.Id}: save failed: {e.Message}");
                failed.Add(new SaveFailureDto(s.Id, e.Message));
            }
        }
        return new PersistenceSaveResult(saved, [.. failed]);
    }

    /// <summary>
    /// 保存一个会话：actor 上编码完整快照并取元数据（编码期间会话暂停处理输出，§8.1），裁剪与写盘在线程池上。
    /// 只保存 persist 的会话；<paramref name="force"/> 为 false 时只保存自上次以来有变化的（§10.4）。
    /// </summary>
    private async Task<bool> SaveAsync(Session s, string reason, bool force)
    {
        if (!PersistenceEnabled || !s.Persist)
        {
            return false;
        }
        var captured = await s.InvokeAsync(() =>
        {
            if (!force && s.Log.Head == s.SavedHead)
            {
                return default((byte[], ReviveMeta, TerminalSize, ulong)?);
            }
            var meta = new ReviveMeta
            {
                PersistentId = s.PersistentId,
                SavedAt = DateTimeOffset.UtcNow,
                Reason = reason,
                ProfileId = s.Create.ProfileId,
                Launch = s.Create.Launch,
                InitialCwd = s.Create.Cwd,
                Cwd = s.Cwd ?? s.Create.Cwd,
                Label = s.Label,
                Title = s.Title,
                Kind = s.Create.Kind,
                Size = new TerminalSizeDto(s.Size.Columns, s.Size.Rows, s.Size.CellWidth, s.Size.CellHeight),
                WidthMode = s.Create.WidthMode,
                Exit = s.Exit is { } x ? new ExitDto(x.ExitCode, x.Signal, x.CoreDumped, x.Message) : null,
                Tags = s.Create.Tags,
            };
            return (s.Core.EncodeSnapshot(), meta, s.Size, s.Log.Head);
        }).ConfigureAwait(false);
        if (captured is not { } c)
        {
            return false;
        }
        var lines = _context.Settings.Persistence.ScrollbackLines;
        await Task.Run(() => _store!.Save(c.Item2, c.Item1, c.Item3, lines)).ConfigureAwait(false);
        s.SavedHead = c.Item4;
        return true;
    }

    /// <summary>定时保存（§10.4）：每 saveIntervalSec 秒，只保存有变化的会话；一次只跑一轮。</summary>
    private void PeriodicSave()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _context.Settings.Persistence.SaveIntervalSec));
        if (!PersistenceEnabled || Stopwatch.GetElapsedTime(_lastPeriodicSave) < interval || Interlocked.Exchange(ref _saving, 1) != 0)
        {
            return;
        }
        _lastPeriodicSave = Stopwatch.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try
            {
                await SaveSessionsAsync(_sessions.Values.ToList(), "periodic", force: false).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _saving, 0);
            }
        });
    }

    private Task SaveForQuitAsync(Session session, string reason) => SaveSessionsAsync([session], reason, force: true);

    private void DeleteRevive(string persistentId)
    {
        try
        {
            _store?.Delete(persistentId);
        }
        catch (Exception e) when (e is IOException or PtyHostException)
        {
            Log.Warn($"delete revive {persistentId}: {e.Message}");
        }
    }

    // ---- 恢复（§10.5）----

    private RevivePayload LoadRevive(string persistentId) =>
        (_store ?? throw new PtyHostException(PtyErrors.PersistenceDisabled, "persistence is disabled")).Load(persistentId);

    /// <summary>
    /// 镜像核心的初始内容：兼容时从二进制快照恢复，否则写入 tail.vt（跨版本回退），都失败就留空。
    /// 之后由 <see cref="ApplyRevive"/> 把恢复的内容推入回滚、写分隔文字、从干净的屏幕开始。
    /// </summary>
    private TerminalCore CreateRevivedCore(RevivePayload revive, TerminalCoreOptions options, ITerminalEffects effects, out RevivedDto revived)
    {
        if (revive.Snapshot is { } snapshot)
        {
            try
            {
                var core = TerminalCore.Restore(snapshot, options, effects);
                core.Resize(options.Size);
                revived = new RevivedDto(revive.Meta.SavedAt, revive.Meta.Lines, UsedFallback: false);
                return core;
            }
            catch (Exception e)
            {
                Log.Warn($"revive {revive.Meta.PersistentId}: snapshot failed ({e.Message}); using tail.vt");
            }
        }
        var fallback = TerminalCore.Create(options, effects);
        if (revive.Vt is { } vt)
        {
            fallback.Write(Encoding.UTF8.GetBytes(vt));
        }
        revived = new RevivedDto(revive.Meta.SavedAt, revive.Meta.Lines, UsedFallback: true);
        return fallback;
    }

    /// <summary>
    /// 恢复之后、启动新 shell 之前追加的输出（经日志，两侧一致，§10.5）：退出备用屏、复位旧程序留下的模式，
    /// 把屏幕内容连同分隔文字推入回滚，然后从干净的屏幕左上角开始——ConPTY 启动时假定屏幕为空、光标在左上角。
    /// </summary>
    private static void ApplyRevive(Session session, string? banner)
    {
        var rows = session.Size.Rows;
        var sb = new StringBuilder();
        sb.Append("\x1b[?1049l\x1b[?1047l\x1b[?47l");                    // 备用屏
        sb.Append("\x1b[?1l\x1b>\x1b[?6l\x1b[?7h\x1b[?25h\x1b[?2004l");   // 光标键、小键盘、原点、自动换行、光标、括号粘贴
        sb.Append("\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1004l\x1b[?1006l\x1b[?2026l\x1b[<u\x1b[0m\x1b[r");
        sb.Append($"\x1b[{rows};1H\n\r");
        if (!string.IsNullOrEmpty(banner))
        {
            sb.Append("\x1b[2m").Append(banner).Append("\x1b[0m");
        }
        sb.Append('\n', rows).Append("\x1b[H\x1b[2J");
        session.InjectOutput(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ---- 退出（§10.6）----

    private async Task<QuitResult> QuitAsync(QuitParams p, ClientConnection caller)
    {
        RaiseQuitRequested(p.Mode);
        var deadline = TimeSpan.FromMilliseconds(p.DeadlineMs ?? 2000);
        var sessions = _sessions.Values.ToList();
        switch (p.Mode)
        {
            case "keepAlive":
                // 也保存一次：防止之后重启或崩溃
                var kept = PersistenceEnabled ? await SaveSessionsAsync(sessions, "quit", force: true, deadline).ConfigureAwait(false) : null;
                return new QuitResult(kept?.Saved ?? 0, 0);
            case "close":
                _store?.Clear();
                _ = Task.Run(() => ShutdownAsync("quit"));
                return new QuitResult(0, sessions.Count);
            default:
                var saved = PersistenceEnabled ? await SaveSessionsAsync(sessions, p.Reason == "user" ? "quit" : p.Reason, force: true, deadline).ConfigureAwait(false) : null;
                _ = Task.Run(() => ShutdownAsync("quit"));
                return new QuitResult(saved?.Saved ?? 0, sessions.Count);
        }
    }

    /// <summary>SIGTERM / 注销（§12.3）：1.5 s 内保存后退出。</summary>
    internal async Task SaveAndShutdownAsync(string reason)
    {
        if (PersistenceEnabled)
        {
            await SaveSessionsAsync(_sessions.Values.ToList(), "shutdown", force: true, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
        }
        await ShutdownAsync(reason).ConfigureAwait(false);
    }
}
