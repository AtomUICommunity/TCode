using System.Threading.Channels;
using TCode.Ai.Messages;

namespace TCode.Ai.Streaming;

/// <summary>
/// 一次模型调用的事件流 + 最终结果。生产者（适配器）推送事件，流本身永不抛异常：
/// 任何失败都以 <see cref="ErrorEvent"/> 结束，<see cref="Result"/> 总会完成。
/// </summary>
public sealed class AssistantMessageStream : IAsyncEnumerable<LlmEvent>
{
    private readonly Channel<LlmEvent> _channel = Channel.CreateUnbounded<LlmEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly TaskCompletionSource<AssistantMessage> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AssistantMessage> Result => _result.Task;

    public void Push(LlmEvent e)
    {
        _channel.Writer.TryWrite(e);
        switch (e)
        {
            case DoneEvent done:
                Complete(done.Message);
                break;
            case ErrorEvent error:
                Complete(error.Message);
                break;
        }
    }

    private void Complete(AssistantMessage message)
    {
        _result.TrySetResult(message);
        _channel.Writer.TryComplete();
    }

    public IAsyncEnumerator<LlmEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
}
