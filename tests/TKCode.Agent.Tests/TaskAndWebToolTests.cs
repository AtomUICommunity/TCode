using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Tasks;
using TKCode.Agent.Coding.Tools.Web;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using Xunit;

namespace TKCode.Agent.Tests;

public sealed class TaskAndWebToolTests
{
    private static async Task<AgentToolResult> Run(IAgentTool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(new ToolInvocation("call", doc.RootElement.Clone()), CancellationToken.None);
    }

    private static string Text(AgentToolResult result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    private static ToolContext NewContext(ToolServices? services = null) =>
        new(new WorkspaceRoots(Directory.CreateTempSubdirectory("tkcode-web-").FullName), "s") { Services = services ?? ToolServices.None };

    [Fact]
    public async Task TaskTools_CreateUpdateDependAndDelete()
    {
        var context = NewContext();
        await Run(new TaskCreateTool(context), """{"subject":"Write parser","description":"Parse input"}""");
        await Run(new TaskCreateTool(context), """{"subject":"Write tests","description":"Cover parser"}""");
        var update = await Run(new TaskUpdateTool(context), """{"task_id":"2","status":"in_progress","addBlockedBy":["1"]}""");
        Assert.Contains("still blocked", Text(update));

        var list = Assert.IsType<TaskListDetails>(ToolDetails.FromJson((await Run(new TaskListTool(context), "{}")).Details));
        Assert.Equal(["1"], list.Tasks.Single(t => t.Id == "2").BlockedBy);
        Assert.Equal(["2"], list.Tasks.Single(t => t.Id == "1").Blocks);

        await Run(new TaskUpdateTool(context), """{"task_id":"1","status":"deleted"}""");
        Assert.Empty(context.Tasks.Items.Single().BlockedBy);
        Assert.True((await Run(new TaskUpdateTool(context), """{"task_id":"9","status":"completed"}""")).IsError);
    }

    [Fact]
    public async Task Tasks_ArePersistedAndRestoredWithSession()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-tasks-").FullName;
        var apis = new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]);
        string file;
        await using (var session = AgentSession.Create(apis, FakeWireApi.Models[1], root, Path.Combine(root, ".s")))
        {
            session.Tools!.Tasks.Create("Persist me", "", null);
            session.Prompt("hi");
            await session.Agent.WaitForIdleAsync();
            file = session.Store.FilePath;
        }
        Assert.Equal("Persist me", Assert.Single(SessionStore.Read(file).Tasks!).Subject);
        await using var restored = AgentSession.Open(apis, FakeWireApi.Models[1], file);
        Assert.Equal("Persist me", Assert.Single(restored.Tools!.Tasks.Items).Subject);
    }

    [Fact]
    public void HtmlToMarkdown_ConvertsCommonStructures()
    {
        const string html = """
            <html><head><title>Doc</title><style>p{}</style></head><body>
            <h1>Title</h1>
            <p>Some <strong>bold</strong> and <em>italic</em> text with <a href="/guide">a link</a> and <code>inline()</code>.</p>
            <ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>
            <ol start="3"><li>three</li></ol>
            <pre><code class="language-csharp">var x = 1;
            Console.WriteLine(x);</code></pre>
            <blockquote><p>quoted</p></blockquote>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>x|y</td></tr></table>
            <script>alert(1)</script><p hidden>secret</p>
            </body></html>
            """;
        var document = new HtmlParser().ParseDocument(html);
        var markdown = new HtmlToMarkdown(new Uri("https://example.com/docs/page")).Convert(document.Body!);

        Assert.Contains("# Title", markdown);
        Assert.Contains("Some **bold** and *italic* text with [a link](https://example.com/guide) and `inline()`.", markdown);
        Assert.Contains("- one\n- two\n  - nested", markdown);
        Assert.Contains("3. three", markdown);
        Assert.Contains("```csharp\nvar x = 1;", markdown);
        Assert.Contains("> quoted", markdown);
        Assert.Contains("| A | B |\n| --- | --- |\n| 1 | x\\|y |", markdown);
        Assert.DoesNotContain("alert", markdown);
        Assert.DoesNotContain("secret", markdown);
    }

    [Fact]
    public void Readability_PrefersArticleAndDropsNavigation()
    {
        var body = string.Concat(Enumerable.Repeat("<p>This is the real article body, with commas, details, and enough text to matter.</p>", 6));
        var html = $"""
            <html><head><title>Article</title></head><body>
            <nav><a href="/">Home</a><a href="/about">About</a></nav>
            <div class="sidebar"><p>Related links and other noise that should go away entirely.</p></div>
            <article>{body}</article>
            <footer>Copyright</footer>
            </body></html>
            """;
        var result = ReadabilityExtractor.Extract(new HtmlParser().ParseDocument(html));
        var markdown = new HtmlToMarkdown(null).Convert(result.Content);
        Assert.Equal("Article", result.Title);
        Assert.Contains("real article body", markdown);
        Assert.DoesNotContain("Home", markdown);
        Assert.DoesNotContain("Copyright", markdown);
        Assert.DoesNotContain("noise", markdown);
    }

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://192.168.1.10/admin")]
    [InlineData("https://[::1]/")]
    [InlineData("https://intranet/")]
    [InlineData("ftp://example.com/file")]
    public void Fetcher_RejectsLocalAndPrivateTargets(string url)
    {
        using var fetcher = new WebFetcher();
        Assert.Throws<WebFetcher.FetchException>(() => fetcher.Normalize(url));
    }

    [Fact]
    public void Fetcher_UpgradesHttp_AndClassifiesPrivateAddresses()
    {
        using var fetcher = new WebFetcher();
        Assert.Equal("https", fetcher.Normalize("http://example.com/a").Scheme);
        Assert.True(WebFetcher.IsPrivate(IPAddress.Parse("10.1.2.3")));
        Assert.True(WebFetcher.IsPrivate(IPAddress.Parse("169.254.169.254")));
        Assert.True(WebFetcher.IsPrivate(IPAddress.Parse("::ffff:192.168.0.1")));
        Assert.False(WebFetcher.IsPrivate(IPAddress.Parse("93.184.216.34")));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task WebFetch_ConvertsHtml_PagesLongContent_AndCaches()
    {
        var paragraph = new string('x', 3000);
        var handler = new StubHandler(_ => Html($"<html><head><title>T</title></head><body><main><p>{paragraph}</p><p>tail-marker</p></main></body></html>"));
        var context = NewContext(new ToolServices { WebFetcher = new WebFetcher(handler) });
        var first = await Run(new WebFetchTool(context), """{"url":"https://example.com/page","maxChars":1000}""");
        Assert.Contains("Title: T", Text(first));
        Assert.Contains("Use offset=1000 to continue", Text(first));

        var rest = await Run(new WebFetchTool(context), """{"url":"https://example.com/page","offset":1000,"maxChars":100000}""");
        Assert.Contains("tail-marker", Text(rest));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WebFetch_FollowsSameHostRedirects_ButReturnsCrossHostOnes()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/old" => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/new", UriKind.Relative) } },
            "/new" => Html("<p>arrived</p>"),
            _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example.org/x") } },
        });
        var context = NewContext(new ToolServices { WebFetcher = new WebFetcher(handler) });
        Assert.Contains("arrived", Text(await Run(new WebFetchTool(context), """{"url":"https://example.com/old"}""")));
        var cross = Text(await Run(new WebFetchTool(context), """{"url":"https://example.com/elsewhere"}"""));
        Assert.Contains("redirected", cross);
        Assert.Contains("https://other.example.org/x", cross);
    }

    [Fact]
    public async Task WebSearch_ParsesProviderResults_AndFiltersDomains()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("secret", request.Headers.GetValues("X-Subscription-Token").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"web":{"results":[
                      {"title":"Docs","url":"https://learn.microsoft.com/dotnet","description":"Official docs"},
                      {"title":"Blog","url":"https://blog.example.com/post","description":"A blog"}]}}
                    """, Encoding.UTF8, "application/json"),
            };
        });
        var provider = WebSearchProviders.Create("brave", "secret", null, handler);
        var context = NewContext(new ToolServices { WebSearch = provider });
        var result = await Run(new WebSearchTool(context), """{"query":"dotnet aot","blockedDomains":["example.com"]}""");
        var text = Text(result);
        Assert.Contains("[Docs](https://learn.microsoft.com/dotnet)", text);
        Assert.DoesNotContain("blog.example.com", text);
        Assert.Single(Assert.IsType<WebSearchDetails>(ToolDetails.FromJson(result.Details)).Results);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Tavily_SendsBearerAndDomains_AndParsesResults()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.tavily.com/search", request.RequestUri!.ToString());
            Assert.Equal("Bearer tvly-key", request.Headers.Authorization!.ToString());
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("""{"query":"q","results":[{"title":"Tavily hit","url":"https://docs.example.com/a","content":"snippet a","score":0.9}]}""");
        });
        var provider = WebSearchProviders.Create("tavily", "tvly-key", null, handler);
        var results = await provider.SearchAsync("aot trimming", 5, ["docs.example.com"], CancellationToken.None);
        Assert.Equal(("Tavily hit", "https://docs.example.com/a", "snippet a"), (results[0].Title, results[0].Url, results[0].Snippet));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("aot trimming", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal("docs.example.com", doc.RootElement.GetProperty("include_domains")[0].GetString());
    }

    [Fact]
    public async Task Exa_SendsApiKeyHeaderAndHighlights_AndJoinsHighlights()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://api.exa.ai/search", request.RequestUri!.ToString());
            Assert.Equal("exa-key", request.Headers.GetValues("x-api-key").Single());
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("""{"results":[{"title":"Exa hit","url":"https://exa.example.com/p","highlights":["first part","second part"]}]}""");
        });
        var provider = WebSearchProviders.Create("exa", "exa-key", null, handler);
        var results = await provider.SearchAsync("semantic", 3, null, CancellationToken.None);
        Assert.Equal("first part … second part", results.Single().Snippet);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(3, doc.RootElement.GetProperty("numResults").GetInt32());
        Assert.True(doc.RootElement.GetProperty("contents").TryGetProperty("highlights", out _));
        Assert.False(doc.RootElement.TryGetProperty("includeDomains", out _));
    }

    [Fact]
    public async Task Searxng_UsesJsonFormatAndSiteFilter_AndExplainsDisabledJson()
    {
        var handler = new StubHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.StartsWith("https://search.local.example/search?", request.RequestUri.ToString());
            Assert.Contains("format=json", query);
            Assert.Contains("site:learn.microsoft.com", query);
            return Json("""{"results":[{"title":"S1","url":"https://learn.microsoft.com/x","content":"c1"},{"title":"S2","url":"https://learn.microsoft.com/y","content":"c2"}]}""");
        });
        var provider = WebSearchProviders.Create("searxng", null, "https://search.local.example/", handler);
        var results = await provider.SearchAsync("nativeaot", 1, ["learn.microsoft.com"], CancellationToken.None);
        Assert.Equal("S1", Assert.Single(results).Title);

        var forbidden = WebSearchProviders.Create("searxng", null, "https://search.local.example", new StubHandler(_ => Json("{}", HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<WebSearchException>(() => forbidden.SearchAsync("x", 3, null, CancellationToken.None));
        Assert.Contains("search.formats", error.Message);
    }

    [Fact]
    public void Providers_RequireKeysAndBaseUrl()
    {
        Assert.Throws<WebSearchException>(() => WebSearchProviders.Create("tavily", null, null));
        Assert.Throws<WebSearchException>(() => WebSearchProviders.Create("exa", "", null));
        Assert.Throws<WebSearchException>(() => WebSearchProviders.Create("searxng", null, null));
        Assert.Throws<WebSearchException>(() => WebSearchProviders.Create("bing", "k", null));
    }

    [Fact]
    public void ToolSet_ControlsAvailableTools()
    {
        var withWeb = NewContext(new ToolServices { WebFetcher = new WebFetcher() });
        var full = BuiltinTools.Create(withWeb, ToolSet.Full).Select(t => t.Name).ToArray();
        Assert.Contains("web_fetch", full);
        Assert.DoesNotContain("web_search", full);
        Assert.Contains("edit", full);
        var readOnly = BuiltinTools.Create(withWeb, ToolSet.ReadOnly).Select(t => t.Name).ToArray();
        Assert.DoesNotContain("edit", readOnly);
        Assert.DoesNotContain("write", readOnly);
    }
}
