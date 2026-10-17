using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TCode.Rpc;
using Xunit;

namespace TCode.Agent.Tests;

public sealed record EchoParams(string Text, int Delay = 0);

public sealed record EchoResult(string Text);

[JsonSerializable(typeof(EchoParams))]
[JsonSerializable(typeof(EchoResult))]
internal sealed partial class TestRpcContext : JsonSerializerContext;

public sealed class JsonRpcConnectionTests
{
    private static (JsonRpcConnection A, JsonRpcConnection B) Pair()
    {
        var aToB = new Pipe();
        var bToA = new Pipe();
        var a = new JsonRpcConnection(bToA.Reader.AsStream(), aToB.Writer.AsStream());
        var b = new JsonRpcConnection(aToB.Reader.AsStream(), bToA.Writer.AsStream());
        return (a, b);
    }

    private static TestRpcContext J => TestRpcContext.Default;

    [Fact]
    public async Task RequestsInBothDirections_AndOrderedNotifications()
    {
        var (a, b) = Pair();
        var received = Channel.CreateUnbounded<string>();
        b.AddRequestHandler("echo", J.EchoParams, J.EchoResult, (p, _) => Task.FromResult(new EchoResult(p.Text + "!")));
        b.AddNotificationHandler("note", J.EchoParams, p => received.Writer.TryWrite(p.Text));
        a.AddRequestHandler("ping", J.EchoParams, J.EchoResult, (p, _) => Task.FromResult(new EchoResult("pong:" + p.Text)));
        a.Start();
        b.Start();

        Assert.Equal("hi!", (await a.InvokeAsync("echo", new EchoParams("hi"), J.EchoParams, J.EchoResult)).Text);
        Assert.Equal("pong:x", (await b.InvokeAsync("ping", new EchoParams("x"), J.EchoParams, J.EchoResult)).Text);

        for (var i = 0; i < 200; i++)
        {
            a.Notify("note", new EchoParams(i.ToString()), J.EchoParams);
        }
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(i.ToString(), await received.Reader.ReadAsync());
        }
    }

    [Fact]
    public async Task Cancellation_IsSentToRemoteHandler()
    {
        var (a, b) = Pair();
        var handlerCancelled = new TaskCompletionSource();
        b.AddRequestHandler("slow", J.EchoParams, J.EchoResult, async (p, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.SetResult();
                throw;
            }
            return new EchoResult("never");
        });
        a.Start();
        b.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.InvokeAsync("slow", new EchoParams("x"), J.EchoParams, J.EchoResult, cts.Token));
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RemoteErrors_ArePropagatedWithCodes()
    {
        var (a, b) = Pair();
        b.AddRequestHandler("boom", J.EchoParams, J.EchoResult, (_, _) => throw new InvalidOperationException("bad state"));
        a.Start();
        b.Start();

        var error = await Assert.ThrowsAsync<RpcRemoteException>(() => a.InvokeAsync("boom", new EchoParams("x"), J.EchoParams, J.EchoResult));
        Assert.Equal(JsonRpcConnection.ServerError, error.Code);
        Assert.Equal("bad state", error.Message);
        Assert.Equal(nameof(InvalidOperationException), error.RemoteType);

        var missing = await Assert.ThrowsAsync<RpcRemoteException>(() => a.InvokeAsync("nope", J.EchoResult));
        Assert.Equal(JsonRpcConnection.MethodNotFound, missing.Code);
    }

    [Fact]
    public async Task Disconnect_FailsPendingCalls()
    {
        var (a, b) = Pair();
        var disconnected = new TaskCompletionSource<string>();
        a.Disconnected += reason => disconnected.TrySetResult(reason);
        b.AddRequestHandler("hang", J.EchoParams, J.EchoResult, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new EchoResult("never");
        });
        a.Start();
        b.Start();

        var call = a.InvokeAsync("hang", new EchoParams("x"), J.EchoParams, J.EchoResult);
        await Task.Delay(50);
        await b.DisposeAsync();

        await Assert.ThrowsAsync<RpcConnectionLostException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(a.IsConnected);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CommandsChangedEvent_RoundTripsThroughSourceGeneratedContext()
    {
        var notification = new SessionEventNotification("s1", 3,
            new CommandsChanged([new RuntimeCommandDto("compact", "Summarize", "<instructions>"), new RuntimeCommandDto("init", null, null)]));
        var json = System.Text.Json.JsonSerializer.Serialize(notification, RpcJsonContext.Default.SessionEventNotification);
        Assert.Contains("\"kind\":\"commandsChanged\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, RpcJsonContext.Default.SessionEventNotification)!;
        var commands = Assert.IsType<CommandsChanged>(back.Event).Commands;
        Assert.Equal(["compact", "init"], commands.Select(c => c.Name));
        Assert.Equal("<instructions>", commands[0].ArgumentHint);
    }
}
