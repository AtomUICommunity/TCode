using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace TCode.Agent.Coding.Tools.Web;

/// <summary>
/// 简化版 Readability：去掉导航、页脚、侧栏、广告等噪声节点，再选出正文。
/// 优先使用 &lt;main&gt; / &lt;article&gt;；否则按段落文本量、逗号数与链接密度给候选节点打分，取最高者。
/// 正文太短时退回整个 body，宁可多给也不丢内容。
/// </summary>
public static partial class ReadabilityExtractor
{
    private const int MinContentLength = 200;

    private static readonly string[] NoiseSelectors =
    [
        "script", "style", "noscript", "template", "svg", "iframe", "form", "button", "nav", "footer", "aside",
        "[role=navigation]", "[role=banner]", "[role=contentinfo]", "[role=complementary]", "[role=dialog]", "[aria-hidden=true]",
    ];

    public sealed record Result(IElement Content, string? Title);

    public static Result Extract(IHtmlDocument document)
    {
        var title = document.Title?.Trim() is { Length: > 0 } t ? t : document.QuerySelector("h1")?.TextContent.Trim();
        var body = document.Body ?? document.DocumentElement;

        foreach (var selector in NoiseSelectors)
        {
            foreach (var element in document.QuerySelectorAll(selector).ToArray())
            {
                if (!ContainsMainContent(element))
                {
                    element.Remove();
                }
            }
        }
        foreach (var element in body.QuerySelectorAll("*").ToArray())
        {
            if (element.LocalName is "body" or "html" or "main" or "article" || element.Parent is null)
            {
                continue;
            }
            var signature = (element.ClassName ?? "") + " " + element.Id;
            if (signature.Length > 1 && Unlikely().IsMatch(signature) && !MaybeContent().IsMatch(signature) && !ContainsMainContent(element))
            {
                element.Remove();
            }
            else if (HtmlToMarkdown.IsHidden(element))
            {
                element.Remove();
            }
        }

        var semantic = body.QuerySelectorAll("main, article, [role=main]")
            .Select(e => (Element: e, Length: TextLength(e)))
            .Where(e => e.Length >= MinContentLength)
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        if (semantic.Element is not null)
        {
            return new Result(semantic.Element, title);
        }

        var best = ScoreCandidates(body);
        return new Result(best is not null && TextLength(best) >= MinContentLength ? best : body, title);
    }

    private static IElement? ScoreCandidates(IElement body)
    {
        var scores = new Dictionary<IElement, double>();
        foreach (var paragraph in body.QuerySelectorAll("p, pre, td, blockquote, li"))
        {
            var text = paragraph.TextContent.Trim();
            if (text.Length < 25)
            {
                continue;
            }
            var score = 1 + text.Count(c => c is ',' or '，' or '、') + Math.Min(text.Length / 100.0, 3);
            if (paragraph.ParentElement is { } parent)
            {
                scores[parent] = scores.GetValueOrDefault(parent, InitialScore(parent)) + score;
                if (parent.ParentElement is { } grandparent)
                {
                    scores[grandparent] = scores.GetValueOrDefault(grandparent, InitialScore(grandparent)) + score / 2;
                }
            }
        }
        return scores
            .Select(kv => (kv.Key, Score: kv.Value * (1 - LinkDensity(kv.Key))))
            .OrderByDescending(kv => kv.Score)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    private static double InitialScore(IElement element)
    {
        var score = element.LocalName switch
        {
            "div" => 5,
            "pre" or "td" or "blockquote" => 3,
            "ol" or "ul" or "dl" or "form" => -3,
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "th" => -5,
            _ => 0,
        };
        var signature = (element.ClassName ?? "") + " " + element.Id;
        if (Positive().IsMatch(signature))
        {
            score += 25;
        }
        if (Negative().IsMatch(signature))
        {
            score -= 25;
        }
        return score;
    }

    private static double LinkDensity(IElement element)
    {
        var length = TextLength(element);
        if (length == 0)
        {
            return 0;
        }
        var linkLength = element.QuerySelectorAll("a").Sum(a => a.TextContent.Trim().Length);
        return Math.Min(1, (double)linkLength / length);
    }

    private static int TextLength(IElement element) => element.TextContent.Trim().Length;

    private static bool ContainsMainContent(IElement element) =>
        element.LocalName is "main" or "article" || element.QuerySelector("main, article, [role=main]") is not null;

    [GeneratedRegex(@"-ad-|ai2html|banner|breadcrumbs|combx|comment|community|cover-wrap|disqus|extra|footer|gdpr|header|legends|menu|related|remark|replies|rss|shoutbox|sidebar|skyscraper|social|sponsor|supplemental|ad-break|agegate|pagination|pager|popup|yom-remote|cookie|newsletter|subscribe|share", RegexOptions.IgnoreCase)]
    private static partial Regex Unlikely();

    [GeneratedRegex(@"and|article|body|column|content|main|shadow|post|entry|text|markdown|prose|doc", RegexOptions.IgnoreCase)]
    private static partial Regex MaybeContent();

    [GeneratedRegex(@"article|body|content|entry|hentry|h-entry|main|page|pagination|post|text|blog|story|markdown|prose|documentation", RegexOptions.IgnoreCase)]
    private static partial Regex Positive();

    [GeneratedRegex(@"-ad-|hidden|^hid$| hid$| hid |^hid |banner|combx|comment|com-|contact|foot|footer|footnote|gdpr|masthead|media|meta|outbrain|promo|related|scroll|share|shoutbox|sidebar|skyscraper|sponsor|shopping|tags|tool|widget", RegexOptions.IgnoreCase)]
    private static partial Regex Negative();
}
