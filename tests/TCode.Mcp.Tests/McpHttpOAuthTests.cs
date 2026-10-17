using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TCode.Agent;
using TCode.Config;
using Xunit;

namespace TCode.Mcp.Tests;

/// <summary>Streamable HTTP 与 OAuth（tests/TCode.Mcp.HttpTestServer：受保护资源元数据 + 模拟授权服务器）。</summary>
public sealed class McpHttpOAuthTests : IAsyncLifetime
{
    private Process? _server;
    private int _port;
    private readonly HttpClient _http = new();

    public async ValueTask InitializeAsync()
    {
        _port = FreePort();
        var dll = TestSupport.ServerDll.Replace("TCode.Mcp.TestServer", "TCode.Mcp.HttpTestServer", StringComparison.Ordinal);
        _server = Process.Start(new ProcessStartInfo("dotnet", [dll, _port.ToString()]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var ready = await _server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("READY", ready);
    }

    public ValueTask DisposeAsync()
    {
        _server?.Kill(entireProcessTree: true);
        _server?.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private McpServerConfig Http(string path) => McpServerConfig.From("web", new McpServerSettings { Url = $"http://127.0.0.1:{_port}{path}", StartupTimeout = 15 },
        McpConfigSource.Global);

    private async Task<int> Stat(string name)
    {
        var stats = JsonDocument.Parse(await _http.GetStringAsync($"http://127.0.0.1:{_port}/stats")).RootElement;
        return stats.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
    }

    [Fact]
    public async Task StreamableHttp_ListsAndCallsTools()
    {
        var config = Http("/mcp");
        await using var manager = new McpManager(new McpHost(), new McpTrustStore(Path.GetTempFileName()), () => [config]);
        var source = manager.CreateSource(TestSupport.Context());
        var tools = (await source.RefreshAsync(true, CancellationToken.None)).Tools.ToDictionary(t => t.Name);
        var result = await tools["mcp__web__echo"].ExecuteAsync(new ToolInvocation("h1", TestSupport.Json("""{"text":"over http"}""")), CancellationToken.None);
        Assert.Equal("echo: over http", TestSupport.Text(result));
        Assert.Equal("2026-07-28", manager.Connections.Single().Status.ProtocolVersion);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task OAuth_NeedsLoginThenLogsInRefreshesAndLogsOut()
    {
        var config = Http("/secure/mcp");
        var authFile = Path.Combine(Directory.CreateTempSubdirectory("tcode-mcp-auth-").FullName, "auth.json");
        var store = new McpAuthStore(authFile);
        var browserOpened = 0;
        // 模拟浏览器：跟随授权服务器的重定向，最终请求回环回调地址
        var oauth = new McpOAuth(store, async (server, url, ct) =>
        {
            Interlocked.Increment(ref browserOpened);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
                await browser.GetAsync(url, ct);
            }, ct);
            await Task.Yield();
            return true;
        });
        await using var manager = new McpManager(new McpHost { OAuth = oauth }, new McpTrustStore(Path.GetTempFileName()), () => [config]);

        // 后台连接：没有令牌 → NeedsAuth，不做动态注册、不打开浏览器
        var connection = manager.Acquire(config, null);
        Assert.False(await connection.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Equal(McpConnectionState.NeedsAuth, connection.State);
        Assert.Equal(0, browserOpened);
        Assert.Equal(0, await Stat("register"));

        // 用户点击“登录”
        await manager.RestartAsync("web", null, interactiveAuth: true);
        Assert.True(await connection.WaitReadyAsync(TimeSpan.FromSeconds(20), CancellationToken.None), string.Join("\n", connection.Log));
        Assert.Equal(1, browserOpened);
        Assert.Equal(1, await Stat("register"));
        var saved = store.Get(config.Url!);
        Assert.Equal("dcr-client", saved?.Tokens?.ClientId);
        Assert.NotNull(saved?.CallbackPort);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(authFile));
        }

        // access token 2 秒过期：之后的调用自动刷新
        await Task.Delay(2500);
        var result = await connection.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = TestSupport.Json("\"after refresh\"") }, null,
            CancellationToken.None);
        Assert.Equal("echo: after refresh", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content.Single()).Text);
        Assert.True(await Stat("token:refresh_token") >= 1);

        // 重连（非交互）：沿用保存的令牌，不再打开浏览器
        await connection.RestartAsync();
        Assert.True(await connection.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Equal(1, browserOpened);

        // 登出：删除令牌后重连 → NeedsAuth
        store.Remove(config.Url!);
        await connection.RestartAsync();
        Assert.False(await connection.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None));
        Assert.Equal(McpConnectionState.NeedsAuth, connection.State);
        manager.Release(connection);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
