using System.Diagnostics;
using System.Threading.Channels;
using TCode.Rpc;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class RpcEndToEndTests
{
    [Fact]
    public async Task HostProcess_StreamsSessionEventsOverStdio()
    {
        // TCODE_TEST_HOST_EXE 指向 NativeAOT 发布的 Host 可执行文件时，验证 AOT 版本的 RPC 链路。
        var aotHost = Environment.GetEnvironmentVariable("TCODE_TEST_HOST_EXE");
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TCode.AgentHost/bin/Debug/net10.0/TCode.AgentHost.dll"));
        var start = aotHost is { Length: > 0 } ? new ProcessStartInfo(aotHost) : new ProcessStartInfo("dotnet", $"\"{hostDll}\"");
        Assert.True(File.Exists(aotHost is { Length: > 0 } ? aotHost : hostDll));

        using var process = Process.Start(new ProcessStartInfo(start.FileName, start.Arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // 隔离数据目录，只使用 Fake 厂商，不读写用户的 ~/.tcode
            Environment =
            {
                ["TCODE_HOME"] = Directory.CreateTempSubdirectory("tcode-rpc-home-").FullName,
                ["TCODE_FAKE_PROVIDER"] = "1",
                // 不探测、不启动用户安装的外部 Agent CLI
                ["TCODE_EXTERNAL_RUNTIMES"] = "0",
            },
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        var client = new RecordingClient();
        await using var rpc = RpcSerialization.CreateConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        AgentHostClientDispatcher.Register(rpc, client);
        var server = new AgentHostServerProxy(rpc);
        rpc.Start();

        try
        {
            var info = await server.InitializeAsync(new InitializeRequest(RpcProtocol.Version, "test"));
            Assert.Equal(RpcProtocol.Version, info.ProtocolVersion);

            var models = await server.ListModelsAsync();
            var cwd = Directory.CreateTempSubdirectory("tcode-rpc-").FullName;
            var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));
            Assert.Equal(models[0].DefaultThinkingLevel, session.Options.ThinkingLevel);
            Assert.Equal(models[0].ContextWindows[0], session.Options.ContextWindow);
            // fake 模型不支持 fast mode：请求会被修正为 false
            // 完全授权：内置 Agent 的非只读工具也不再询问
            var options = await server.SetOptionsAsync(session.SessionId, new SessionOptionsDto("Low", FastMode: true, PermissionMode: "fullAccess"));
            Assert.Equal(new SessionOptionsDto("Low", false, models[0].ContextWindows[0], "fullAccess", "agent"), options);
            var result = await server.PromptAsync(new PromptRequest(session.SessionId, "看看目录里有哪些文件"));
            Assert.True(result.Started);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var seen = new List<SessionEvent>();
            await foreach (var n in client.Events.Reader.ReadAllAsync(timeout.Token))
            {
                seen.Add(n.Event);
                if (n.Event is AgentEnded)
                {
                    break;
                }
            }

            Assert.Contains(seen, e => e is TextDelta);
            Assert.Contains(seen, e => e is ThinkingDelta);
            Assert.Contains(seen, e => e is ToolExecutionEnded { IsError: false, ToolName: "ls" });
            var listed = Assert.Single(await server.ListSessionsAsync(cwd));
            Assert.Equal(session.SessionId, listed.SessionId);
            Assert.Equal("看看目录里有哪些文件", listed.Title);
            Assert.Equal(models[0].Ref, listed.ModelId);

            // 已打开的会话直接返回同一个实例，历史含工具调用与结果
            var reopened = await server.OpenSessionAsync(listed.FilePath);
            Assert.Equal(session.SessionId, reopened.Session.SessionId);
            Assert.Contains(reopened.History, m => m.Blocks.Any(b => b is HistoryToolResult { ToolName: "ls" }));

            // 关闭后从文件恢复：参数沿用最后一次设置
            await server.CloseSessionAsync(session.SessionId);
            var restored = await server.OpenSessionAsync(listed.FilePath);
            Assert.Equal("Low", restored.Session.Options.ThinkingLevel);
            // 完全授权不沿用：重新打开的会话回到“询问”
            Assert.Equal("ask", restored.Session.Options.PermissionMode);
            Assert.Equal(reopened.History.Count, restored.History.Count);
            await server.CloseSessionAsync(restored.Session.SessionId);

            // 历史分页：打开时只取最近 1 轮（本会话只有 1 轮，因此从 0 开始）
            var paged = await server.OpenSessionAsync(listed.FilePath, 1);
            Assert.Equal(0, paged.HistoryStart);
            Assert.Equal(restored.History.Count, paged.History.Count);
            var earlier = await server.GetHistoryAsync(paged.Session.SessionId, paged.HistoryStart, 10);
            Assert.Empty(earlier.History);
            await server.CloseSessionAsync(paged.Session.SessionId);

            // 会话管理：分页、重命名、置顶、在新会话中继续、删除
            var page = await server.QuerySessionsAsync(cwd, 0, 10);
            Assert.Equal(1, page.Total);
            Assert.Null(page.NextOffset);
            await server.RenameSessionAsync(listed.FilePath, "扫描目录");
            await server.PinSessionAsync(listed.FilePath, true);
            var renamed = Assert.Single((await server.QuerySessionsAsync(cwd, 0, 10)).Items);
            Assert.Equal("扫描目录", renamed.Title);
            Assert.True(renamed.HasCustomTitle);
            Assert.True(renamed.IsPinned);

            var fork = await server.ForkSessionAsync(listed.FilePath);
            Assert.NotEqual(session.SessionId, fork.Session.SessionId);
            Assert.Equal(restored.History.Count, fork.History.Count);
            await server.CloseSessionAsync(fork.Session.SessionId);
            var two = await server.QuerySessionsAsync(cwd, 0, 1);
            Assert.Equal(2, two.Total);
            Assert.Equal(1, two.NextOffset);
            Assert.True(two.Items[0].IsPinned); // 置顶在前

            await server.DeleteSessionAsync(listed.FilePath);
            Assert.False(File.Exists(listed.FilePath));
            var left = Assert.Single((await server.QuerySessionsAsync(cwd, 0, 10)).Items);
            Assert.Equal(fork.Session.SessionId, left.SessionId);
            await Assert.ThrowsAsync<RpcRemoteException>(() => server.DeleteSessionAsync("/etc/hosts"));
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed class RecordingClient : IAgentHostClient
    {
        public Channel<SessionEventNotification> Events { get; } = Channel.CreateUnbounded<SessionEventNotification>();

        public void OnSessionEvent(SessionEventNotification notification) => Events.Writer.TryWrite(notification);

        public void OnModelsChanged(ModelsChangedNotification notification)
        {
        }

        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolApprovalResponse(true));

        public void OnMcpChanged(McpChangedNotification notification)
        {
        }

        public void OnAssetsChanged(AssetsChangedNotification notification)
        {
        }

        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new McpElicitResponse("cancel"));

        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new McpOpenUrlResponse(false));
    }
}
