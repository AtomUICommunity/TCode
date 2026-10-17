using System.Text;
using System.Text.Json;

namespace TKCode.Agent.Coding.Tools.Web;

/// <summary>网页搜索（客户端工具，后端由 settings 的 webSearch 配置）。结果为标题、URL 与摘要。</summary>
public sealed class WebSearchTool(ToolContext context) : IAgentTool
{
    private const int DefaultLimit = 8;

    public string Name => "web_search";
    public string Label => "Tool.WebSearch";

    public string Description =>
        "Search the web and return result titles, URLs and snippets. Use it for information beyond your knowledge cutoff or that changes frequently. " +
        "Optionally restrict results to allowedDomains or exclude blockedDomains. Use web_fetch to read a result in full.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"The search query."},
          "allowedDomains":{"type":"array","items":{"type":"string"},"description":"Only include results from these domains."},
          "blockedDomains":{"type":"array","items":{"type":"string"},"description":"Exclude results from these domains."},
          "limit":{"type":"integer","minimum":1,"maximum":20,"description":"Maximum number of results (default 8)."}},
         "required":["query"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Search the web";

    public IReadOnlyList<string> PromptGuidelines =>
        ["When you use web_search results in an answer, list the source URLs you relied on."];

    public ToolPermission DescribePermission(JsonElement arguments) => new(ToolAccess.Network, Title: "Search: " + arguments.Required("query"));

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var provider = context.Services.WebSearch ?? throw new InvalidOperationException("web_search is not configured.");
        var args = invocation.Arguments;
        var query = args.Required("query");
        var limit = Math.Clamp(args.Int("limit") ?? DefaultLimit, 1, 20);
        var allowed = args.StringArray("allowedDomains");
        var blocked = args.StringArray("blockedDomains");

        IReadOnlyList<WebSearchResult> results;
        try
        {
            results = await provider.SearchAsync(query, limit, allowed, cancellationToken);
        }
        catch (WebSearchException ex)
        {
            return AgentToolResult.Error(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return AgentToolResult.Error($"Search request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentToolResult.Error("Search request timed out.");
        }

        results = results
            .Where(r => allowed is not { Count: > 0 } || allowed.Any(d => MatchesDomain(r.Url, d)))
            .Where(r => blocked is not { Count: > 0 } || !blocked.Any(d => MatchesDomain(r.Url, d)))
            .Take(limit)
            .ToArray();
        var details = new WebSearchDetails(query, provider.Name, results).ToJson();
        if (results.Count == 0)
        {
            return AgentToolResult.Text($"No results for \"{query}\".") with { Details = details };
        }
        var sb = new StringBuilder($"Search results for \"{query}\":\n");
        foreach (var r in results)
        {
            sb.Append("\n## [").Append(r.Title.Length > 0 ? r.Title : r.Url).Append("](").Append(r.Url).Append(")\n");
            if (r.Snippet is { Length: > 0 } snippet)
            {
                sb.Append(snippet.Trim()).Append('\n');
            }
        }
        return AgentToolResult.Text(sb.ToString()) with { Details = details };
    }

    public static bool MatchesDomain(string url, string domain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var host = uri.IdnHost.ToLowerInvariant();
        var d = domain.Trim().ToLowerInvariant().TrimStart('.');
        if (d.StartsWith("http", StringComparison.Ordinal) && Uri.TryCreate(d, UriKind.Absolute, out var du))
        {
            d = du.IdnHost;
        }
        return host == d || host.EndsWith("." + d, StringComparison.Ordinal);
    }
}
