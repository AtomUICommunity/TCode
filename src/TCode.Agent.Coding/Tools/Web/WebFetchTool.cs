using System.Text;
using System.Text.Json;

namespace TCode.Agent.Coding.Tools.Web;

/// <summary>抓取网页并以 Markdown 返回正文；长内容按字符分页（offset）。跨主机重定向不自动跟随。</summary>
public sealed class WebFetchTool(ToolContext context) : IAgentTool
{
    private const int DefaultMaxChars = 50_000;
    private const int MaxMaxChars = 100_000;

    public string Name => "web_fetch";
    public string Label => "Tool.WebFetch";

    public string Description =>
        "Fetch a web page and return its main content as Markdown (HTML is cleaned up and converted; Markdown, text, JSON and XML are returned as-is). " +
        "http URLs are upgraded to https; local and private network addresses are refused. " +
        $"Content is limited to maxChars characters (default {DefaultMaxChars}); use offset to read further. " +
        "Redirects to a different host are not followed: the target URL is returned so you can fetch it explicitly. Results are cached for 15 minutes.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "url":{"type":"string","description":"The absolute URL to fetch."},
          "offset":{"type":"integer","minimum":0,"description":"Character offset to start from (for paging through long pages)."},
          "maxChars":{"type":"integer","minimum":1000,"maximum":100000,"description":"Maximum characters to return (default 50000)."}},
         "required":["url"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Fetch a web page as Markdown";

    public IReadOnlyList<string> PromptGuidelines =>
        ["Only fetch URLs that the user provided or that came from search results or documents you read; do not guess URLs."];

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        var url = arguments.Required("url");
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost : url;
        return new(ToolAccess.Network, Domain: host, Title: url);
    }

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var fetcher = context.Services.WebFetcher ?? throw new InvalidOperationException("web_fetch is not available.");
        var args = invocation.Arguments;
        var url = args.Required("url");
        var offset = Math.Max(0, args.Int("offset") ?? 0);
        var maxChars = Math.Clamp(args.Int("maxChars") ?? DefaultMaxChars, 1000, MaxMaxChars);

        WebFetcher.FetchResult result;
        try
        {
            result = await fetcher.FetchAsync(url, cancellationToken);
        }
        catch (WebFetcher.FetchException ex)
        {
            return AgentToolResult.Error(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return AgentToolResult.Error($"Request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentToolResult.Error("Request timed out after 30 seconds.");
        }

        if (result.RedirectTo is { } redirect)
        {
            return AgentToolResult.Text(
                $"{result.Url} redirected ({result.StatusCode}) to a different host: {redirect}\n" +
                "Call web_fetch again with that URL if you want to follow the redirect.");
        }

        var content = result.Markdown ?? "";
        var sb = new StringBuilder();
        sb.Append("URL: ").Append(result.Url).Append('\n');
        if (result.Title is { Length: > 0 } title)
        {
            sb.Append("Title: ").Append(title).Append('\n');
        }
        if (result.StatusCode is < 200 or >= 300)
        {
            sb.Append("HTTP status: ").Append(result.StatusCode).Append('\n');
        }
        sb.Append('\n');
        if (offset >= content.Length && content.Length > 0)
        {
            return AgentToolResult.Error($"offset {offset} is beyond the end of the content ({content.Length} characters).");
        }
        var end = Math.Min(content.Length, offset + maxChars);
        sb.Append(content, offset, end - offset);
        if (end < content.Length || offset > 0)
        {
            sb.Append($"\n\n[Showing characters {offset}-{end} of {content.Length}.{(end < content.Length ? $" Use offset={end} to continue." : "")}]");
        }
        return new AgentToolResult([new TCode.Ai.Messages.TextContent(sb.ToString())])
        {
            IsError = result.StatusCode >= 400,
            Details = new WebFetchDetails(result.Url.ToString(), result.StatusCode, result.Title, result.ContentType, content.Length).ToJson(),
        };
    }
}
