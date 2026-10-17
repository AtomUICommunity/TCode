using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels.Completion;

/// <summary>@ 补全：MCP 服务器提供的资源，选中后成为资源 tag（docs/design/mcp.md §8.1）。</summary>
public sealed class McpResourceProvider(McpComposerViewModel mcp) : ICompletionProvider
{
    public char Trigger => '@';

    public int Order => 50;

    public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var catalog = await mcp.GetCatalogAsync();
        var items = new List<CompletionItem>();
        foreach (var resource in catalog.Resources)
        {
            var title = resource.Title ?? resource.Name;
            // 标题命中优先（用于高亮），否则匹配 服务器:URI
            var match = FuzzyMatcher.Match(query, title) ?? (FuzzyMatcher.Match(query, McpComposerViewModel.ResourceTagValue(resource)) is { } other
                ? other with { Positions = [] } : null);
            if (match is not { } m)
            {
                continue;
            }
            var tag = new TagSegment(TagKind.McpResource, McpComposerViewModel.ResourceTagValue(resource), title);
            items.Add(new CompletionItem(title, resource.Uri, LucideKind.Plug, resource.Server, new CompletionAction(tag), m.Score, m.Positions));
        }
        return items;
    }
}

/// <summary>/ 补全：MCP 服务器提供的提示词，选中后在输入框上方展开参数（docs/design/mcp.md §8.2）。</summary>
public sealed class McpPromptProvider(McpComposerViewModel mcp) : ICompletionProvider
{
    public char Trigger => '/';

    public int Order => 50;

    public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        if (mcp.HasPrompt)
        {
            return [];
        }
        var catalog = await mcp.GetCatalogAsync();
        var items = new List<CompletionItem>();
        foreach (var prompt in catalog.Prompts)
        {
            var title = $"{prompt.Server}:{prompt.Name}";
            if (FuzzyMatcher.Match(query, title, prompt.Server.Length + 1) is not { } match)
            {
                continue;
            }
            var chosen = prompt;
            items.Add(new CompletionItem(title, prompt.Title ?? prompt.Description, LucideKind.SquareTerminal, "MCP",
                new CompletionAction(Run: () => mcp.SelectPrompt(chosen)), match.Score, match.Positions));
        }
        return items;
    }
}
