// 测试用的 HTTP MCP 服务器（ASP.NET Core，JIT）：
//   /mcp         无需认证（Streamable HTTP，无状态）
//   /secure/mcp  需要 OAuth：受保护资源元数据 + 模拟授权服务器（DCR、PKCE S256、iss、refresh token，access token 2 秒过期）
//   /stats       各端点的调用次数（测试用来确认没有在后台做动态注册）
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var port = int.Parse(args[0]);
var issuer = $"http://127.0.0.1:{port}";
var codes = new ConcurrentDictionary<string, string>();
var validTokens = new ConcurrentDictionary<string, bool>();
var stats = new ConcurrentDictionary<string, int>();
var tokenCounter = 0;
void Count(string name) => stats.AddOrUpdate(name, 1, (_, n) => n + 1);

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(issuer);
builder.Services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "tcode-http-test", Version = "1.0.0" })
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<HttpTools>();
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/secure/mcp"))
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
        if (token is null || !validTokens.ContainsKey(token))
        {
            Count("401");
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{issuer}/.well-known/oauth-protected-resource/secure/mcp\", scope=\"mcp.read\"";
            return;
        }
        Count("authorized");
    }
    await next();
});
app.MapGet("/stats", () => Results.Json(stats));
app.MapGet("/.well-known/oauth-protected-resource/secure/mcp", () => Results.Json(new Dictionary<string, object>
{
    ["resource"] = $"{issuer}/secure/mcp", ["authorization_servers"] = new[] { issuer }, ["scopes_supported"] = new[] { "mcp.read" },
}));
app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new Dictionary<string, object>
{
    ["issuer"] = issuer, ["authorization_endpoint"] = $"{issuer}/authorize", ["token_endpoint"] = $"{issuer}/token",
    ["registration_endpoint"] = $"{issuer}/register", ["response_types_supported"] = new[] { "code" },
    ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" }, ["code_challenge_methods_supported"] = new[] { "S256" },
    ["token_endpoint_auth_methods_supported"] = new[] { "none" }, ["authorization_response_iss_parameter_supported"] = true,
}));
app.MapPost("/register", async (HttpContext ctx) =>
{
    Count("register");
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
    return Results.Json(new Dictionary<string, object>
    {
        ["client_id"] = "dcr-client", ["redirect_uris"] = doc.RootElement.GetProperty("redirect_uris").Clone(), ["token_endpoint_auth_method"] = "none",
    }, statusCode: 201);
});
app.MapGet("/authorize", (HttpContext ctx) =>
{
    Count("authorize");
    var q = ctx.Request.Query;
    if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["resource"]))
    {
        return Results.BadRequest();
    }
    var code = "code-" + Guid.NewGuid().ToString("N")[..8];
    codes[code] = q["code_challenge"].ToString();
    return Results.Redirect(QueryHelpers.AddQueryString(q["redirect_uri"].ToString(),
        new Dictionary<string, string?> { ["code"] = code, ["state"] = q["state"], ["iss"] = issuer }));
});
app.MapPost("/token", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var grant = form["grant_type"].ToString();
    Count("token:" + grant);
    if (grant == "authorization_code")
    {
        if (!codes.TryRemove(form["code"].ToString(), out var challenge)
            || Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != challenge)
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }
    }
    else if (grant != "refresh_token" || !form["refresh_token"].ToString().StartsWith("rt-", StringComparison.Ordinal))
    {
        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
    }
    var n = Interlocked.Increment(ref tokenCounter);
    validTokens[$"at-{n}"] = true;
    return Results.Json(new Dictionary<string, object> { ["access_token"] = $"at-{n}", ["token_type"] = "Bearer", ["expires_in"] = 2, ["refresh_token"] = $"rt-{n}" });
});
app.MapMcp("/mcp");
app.MapMcp("/secure/mcp");

await app.StartAsync();
Console.WriteLine("READY");
await app.WaitForShutdownAsync();

[McpServerToolType]
public sealed class HttpTools
{
    [McpServerTool(Name = "echo"), Description("Echo text back")]
    public static string Echo(string text) => $"echo: {text}";
}
