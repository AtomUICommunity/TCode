namespace TKCode.Rpc;

public static class RpcSerialization
{
    /// <summary>创建基于 stdio（或任意一对流）的 JSON-RPC 连接。注册处理器后调用 <see cref="JsonRpcConnection.Start"/>。</summary>
    public static JsonRpcConnection CreateConnection(Stream sendingStream, Stream receivingStream) =>
        new(receivingStream, sendingStream);
}
