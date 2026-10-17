using TCode.Terminal.Host.Sessions;
using TCode.Terminal.Protocol;

namespace TCode.Terminal.Host;

public sealed partial class PtyHostServer
{
    // 持久化（04 §10）在 M2 第 5 步实现；这里先给出空实现。
    private partial bool HandleExtension(ClientConnection c, uint id, uint sessionId, string method, Envelope e) => false;

    private object? LoadRevive(string persistentId) => throw new PtyHostException(PtyErrors.ReviveNotFound, $"revive entry {persistentId} not found");

    private static void ApplyRevive(Session session, object revive, string? banner)
    {
    }

    private void DeleteRevive(string persistentId)
    {
    }

    private Task SaveForQuitAsync(Session session, string reason) => Task.CompletedTask;

    private async Task<QuitResult> QuitAsync(QuitParams p, ClientConnection caller)
    {
        RaiseQuitRequested(p.Mode);
        if (p.Mode == "keepAlive")
        {
            return new QuitResult(0, 0);
        }
        var closed = _sessions.Count;
        _ = Task.Run(() => ShutdownAsync("quit"));
        await Task.Yield();
        return new QuitResult(0, closed);
    }
}
