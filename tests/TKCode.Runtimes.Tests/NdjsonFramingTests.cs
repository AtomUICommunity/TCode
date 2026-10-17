using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using TKCode.Rpc;
using TKCode.Runtimes.Discovery;
using Xunit;

namespace TKCode.Runtimes.Tests;

public sealed class NdjsonFramingTests
{
    [Fact]
    public async Task LineReader_SplitsLines_AcrossBufferBoundaries()
    {
        var big = new string('x', 100_000);
        var data = Encoding.UTF8.GetBytes($"a\r\n{big}\n\nlast");
        var reader = new NdjsonLineReader(new MemoryStream(data), bufferSize: 16);
        Assert.Equal("a", Encoding.UTF8.GetString((await reader.ReadLineAsync())!));
        Assert.Equal(big, Encoding.UTF8.GetString((await reader.ReadLineAsync())!));
        Assert.Empty((await reader.ReadLineAsync())!);
        Assert.Equal("last", Encoding.UTF8.GetString((await reader.ReadLineAsync())!));
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task NewlineConnection_WithoutVersionField_IgnoresNoise_AndUsesFallbacks()
    {
        var toPeer = new Pipe();
        var fromPeer = new Pipe();
        var rpc = new JsonRpcConnection(fromPeer.Reader.AsStream(), toPeer.Writer.AsStream(),
            new JsonRpcConnectionOptions { Framing = JsonRpcFraming.NewlineDelimited, WriteVersionField = false, SendCancelNotifications = false });
        var notes = new List<string>();
        rpc.SetFallbackNotificationHandler((method, p) => notes.Add(method + ":" + p?.GetProperty("n").GetInt32()));
        rpc.SetFallbackRequestHandler((method, p, ct) =>
            method == "approve"
                ? Task.FromResult<JsonElement?>(JsonDocument.Parse("""{"decision":"accept"}""").RootElement)
                : throw new RpcRemoteException(JsonRpcConnection.MethodNotFound, "nope"));
        rpc.Start();

        var call = rpc.InvokeRawAsync("thread/start", JsonDocument.Parse("""{"cwd":"/x"}""").RootElement);
        var peerIn = new StreamReader(toPeer.Reader.AsStream());
        var sent = await peerIn.ReadLineAsync();
        Assert.Equal("""{"id":1,"method":"thread/start","params":{"cwd":"/x"}}""", sent);

        var peerOut = fromPeer.Writer.AsStream();
        await peerOut.WriteAsync(Encoding.UTF8.GetBytes(
            "some log line on stdout\n" +
            """{"method":"turn/started","params":{"n":1}}""" + "\n" +
            """{"id":1,"result":{"thread":{"id":"t1"}}}""" + "\n" +
            """{"id":"srv-1","method":"approve","params":{}}""" + "\n" +
            """{"id":"srv-2","method":"unknown","params":{}}""" + "\n"));
        await peerOut.FlushAsync();

        var result = await call;
        Assert.Equal("t1", result.GetProperty("thread").GetProperty("id").GetString());
        // 请求并发处理，回复顺序不固定
        var replies = new[] { await peerIn.ReadLineAsync(), await peerIn.ReadLineAsync() };
        Assert.Contains("""{"id":"srv-1","result":{"decision":"accept"}}""", replies);
        var error = JsonDocument.Parse(replies.Single(r => r!.Contains("srv-2"))!).RootElement;
        Assert.Equal(JsonRpcConnection.MethodNotFound, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(["turn/started:1"], notes);
        await rpc.DisposeAsync();
    }

    [Fact]
    public void Locator_PrefersOverride_ThenPath_ThenLoginShellPath()
    {
        var dir1 = Directory.CreateTempSubdirectory("loc1-").FullName;
        var dir2 = Directory.CreateTempSubdirectory("loc2-").FullName;
        var inShell = Touch(Path.Combine(dir2, "mycli"));
        var env = new Dictionary<string, string?> { ["PATH"] = dir1, ["HOME"] = dir1 };
        var locator = new ExecutableLocator(k => env.GetValueOrDefault(k), () => [dir2]);

        Assert.Equal(inShell, locator.Find("mycli"));
        var inPath = Touch(Path.Combine(dir1, "mycli"));
        Assert.Equal(inPath, locator.Find("mycli"));
        env["TKCODE_MYCLI_PATH"] = inShell;
        Assert.Equal(inShell, locator.Find("mycli", "TKCODE_MYCLI_PATH"));
        env["TKCODE_MYCLI_PATH"] = Path.Combine(dir1, "missing");
        Assert.Null(locator.Find("mycli", "TKCODE_MYCLI_PATH"));
        Assert.StartsWith(dir2 + Path.PathSeparator + dir1, locator.ChildPath(inShell));
    }

    private static string Touch(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}
