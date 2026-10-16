using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCode.Rpc;

/// <summary>RPC 全部载荷的源生成序列化上下文（NativeAOT 安全）。新增 DTO 时在此登记。</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    UseStringEnumConverter = true)]
// UI → Host
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(IReadOnlyList<ModelDto>))]
[JsonSerializable(typeof(ProvidersSnapshotDto))]
[JsonSerializable(typeof(IReadOnlyList<CatalogProviderDto>))]
[JsonSerializable(typeof(ProviderTestRequest))]
[JsonSerializable(typeof(ProviderTestResult))]
[JsonSerializable(typeof(ProviderIdParams))]
[JsonSerializable(typeof(CatalogStatusDto))]
[JsonSerializable(typeof(CatalogUpdateDto))]
[JsonSerializable(typeof(IReadOnlyList<RuntimeDto>))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(ListSessionsParams))]
[JsonSerializable(typeof(QuerySessionsParams))]
[JsonSerializable(typeof(SessionPageDto))]
[JsonSerializable(typeof(RenameSessionParams))]
[JsonSerializable(typeof(PinSessionParams))]
[JsonSerializable(typeof(SessionFileParams))]
[JsonSerializable(typeof(HistoryPageParams))]
[JsonSerializable(typeof(HistoryPageDto))]
[JsonSerializable(typeof(IReadOnlyList<SessionSummaryDto>))]
[JsonSerializable(typeof(OpenSessionParams))]
[JsonSerializable(typeof(OpenSessionResult))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResult))]
[JsonSerializable(typeof(SessionIdParams))]
[JsonSerializable(typeof(SetModelParams))]
[JsonSerializable(typeof(SetOptionsParams))]
[JsonSerializable(typeof(SessionOptionsDto))]
// Host → UI
[JsonSerializable(typeof(SessionEventNotification))]
[JsonSerializable(typeof(ModelsChangedNotification))]
[JsonSerializable(typeof(ToolApprovalRequest))]
[JsonSerializable(typeof(ToolApprovalResponse))]
public sealed partial class RpcJsonContext : JsonSerializerContext;
