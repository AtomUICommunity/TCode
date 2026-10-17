// 测试用的 MCP stdio 服务器（基于 ModelContextProtocol.Core 的底层处理器，不使用反射发现工具）。
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

Console.Error.WriteLine($"[test-server] started pid={Environment.ProcessId}");
if (Environment.GetEnvironmentVariable("MCP_TEST_CRASH") == "1")
{
    Console.Error.WriteLine("[test-server] crashing on purpose");
    return 3;
}

static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

var extraToolAdded = false;

List<Tool> Tools()
{
    var tools = new List<Tool>
    {
        new() { Name = "echo", Title = "Echo", Description = "Echo text back", InputSchema = Schema("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = true } },
        new() { Name = "env", Description = "Report MCP_TEST_VAR and the working directory", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "slow", Description = "Reports progress; honours cancellation", InputSchema = Schema("""{"type":"object","properties":{"steps":{"type":"integer"}}}""") },
        new() { Name = "add_tool", Description = "Adds a tool and sends tools/list_changed", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "image", Description = "Returns a 1x1 PNG", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "big", Description = "Returns a large text", InputSchema = Schema("""{"type":"object","properties":{"chars":{"type":"integer"}}}""") },
        new() { Name = "fail", Description = "Returns isError", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "structured", Description = "Returns structuredContent only", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "ask", Description = "Asks the user for a name via elicitation", InputSchema = Schema("""{"type":"object"}""") },
        new() { Name = "weird.name with spaces", Description = "Name needs sanitizing", InputSchema = Schema("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""") },
    };
    if (extraToolAdded)
    {
        tools.Add(new Tool { Name = "extra", Description = "Added at runtime", InputSchema = Schema("""{"type":"object"}""") });
    }
    return tools;
}

const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "tkcode-test-server", Version = "1.0.0" },
    ServerInstructions = "Use echo to repeat text.",
    Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability { ListChanged = true },
        Resources = new ResourcesCapability(),
        Prompts = new PromptsCapability(),
    },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools() }),
        ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
        {
            Resources =
            [
                new Resource { Uri = "test://readme", Name = "README", Title = "Project readme", MimeType = "text/markdown" },
                new Resource { Uri = "test://pixel", Name = "pixel.png", MimeType = "image/png" },
            ],
        }),
        ReadResourceHandler = (ctx, _) => ValueTask.FromResult(new ReadResourceResult
        {
            Contents = ctx.Params!.Uri switch
            {
                "test://readme" => [new TextResourceContents { Uri = "test://readme", MimeType = "text/markdown", Text = "# Readme\nHello resource." }],
                "test://pixel" => [BlobResourceContents.FromBytes(Convert.FromBase64String(Png1x1), "test://pixel", "image/png")],
                _ => throw new McpProtocolException("Resource not found", McpErrorCode.InvalidParams),
            },
        }),
        ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult
        {
            Prompts =
            [
                new Prompt
                {
                    Name = "review", Title = "Code review", Description = "Review a file",
                    Arguments = [new PromptArgument { Name = "file", Description = "File to review", Required = true }],
                },
            ],
        }),
        GetPromptHandler = (ctx, _) => ValueTask.FromResult(new GetPromptResult
        {
            Messages =
            [
                new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = $"Please review {ctx.Params!.Arguments?["file"]}." } },
                new PromptMessage { Role = Role.Assistant, Content = new TextContentBlock { Text = "Sure, send it." } },
            ],
        }),
        CallToolHandler = async (ctx, ct) =>
        {
            var p = ctx.Params!;
            string? Arg(string name) => p.Arguments is { } a && a.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? IntArg(string name) => p.Arguments is { } a && a.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            switch (p.Name)
            {
                case "echo":
                    return Text($"echo: {Arg("text")}");
                case "env":
                    return Text($"MCP_TEST_VAR={Environment.GetEnvironmentVariable("MCP_TEST_VAR")}; cwd={Environment.CurrentDirectory}");
                case "slow":
                    var steps = IntArg("steps") ?? 3;
                    for (var i = 1; i <= steps; i++)
                    {
                        await Task.Delay(100, ct);
                        if (p.ProgressToken is { } token)
                        {
                            await ctx.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = i, Total = steps, Message = $"step {i}" },
                                cancellationToken: ct);
                        }
                    }
                    return Text($"slow done after {steps} steps");
                case "add_tool":
                    extraToolAdded = true;
                    await ctx.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
                    return Text("added");
                case "image":
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "a pixel" }, ImageContentBlock.FromBytes(Convert.FromBase64String(Png1x1), "image/png")],
                    };
                case "big":
                    return Text(new string('x', IntArg("chars") ?? 150_000));
                case "fail":
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "bad input: try again" }] };
                case "structured":
                    return new CallToolResult { StructuredContent = Schema("""{"temperature":22.5}""") };
                case "ask":
                    var answer = await ctx.Server.ElicitAsync(new ElicitRequestParams
                    {
                        Message = "What is your name?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["name"] = new ElicitRequestParams.StringSchema { Title = "Name" },
                            },
                            Required = ["name"],
                        },
                    }, ct);
                    return Text(answer.IsAccepted && answer.Content is { } c && c.TryGetValue("name", out var n) ? $"hello {n.GetString()}" : $"elicitation {answer.Action}");
                case "weird.name with spaces":
                    return Text("weird ok");
                default:
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"unknown tool {p.Name}" }] };
            }
        },
    },
};

await using var server = McpServer.Create(new StdioServerTransport(options), options);
await server.RunAsync();
Console.Error.WriteLine("[test-server] exiting");
return 0;

static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
