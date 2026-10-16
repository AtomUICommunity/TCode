using System.Net;

namespace TCode.Ai.Http;

/// <summary>厂商返回的错误（HTTP 状态或流中的 error 事件）。</summary>
public sealed class ProviderException(string message, HttpStatusCode? status = null, bool isRetryable = false)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool IsRetryable { get; } = isRetryable;
}
