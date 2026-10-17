using System.Text.Json;
using TCode.Agent.Coding.Tools;
using TCode.Agent.Coding.Tools.Questions;
using TCode.Agent.Questions;
using Xunit;

namespace TCode.Agent.Tests;

/// <summary>ask_user 的参数校验、回答编码与结果文本（docs/design/ask-user.md §4、§5.4）。</summary>
public sealed class AskUserToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static string Question(string text, string options, bool multi = false) =>
        $$"""{"question":"{{text}}","header":"H","multiSelect":{{(multi ? "true" : "false")}},"options":[{{options}}]}""";

    private const string TwoOptions = """{"label":"A","description":"a"},{"label":"B","description":"b"}""";

    [Fact]
    public void Parse_ValidatesCountsAndUniqueness()
    {
        var questions = AskUserTool.Parse(Args($$"""{"questions":[{{Question("Q1?", TwoOptions)}},{{Question("Q2?", TwoOptions, multi: true)}}]}"""))!;
        Assert.Equal(["Q1?", "Q2?"], questions.Select(q => q.Id));
        Assert.True(questions[1].MultiSelect);
        Assert.Equal("a", questions[0].Options[0].Description);

        // 少于两个选项：不打扰用户，返回 null（由工具给出“不要编造第二个选项”的提示）
        Assert.Null(AskUserTool.Parse(Args($$"""{"questions":[{{Question("Q?", """{"label":"Only","description":"x"}""")}}]}""")));
        Assert.Throws<ToolArgumentException>(() => AskUserTool.Parse(Args("""{"questions":[]}""")));
        Assert.Throws<ToolArgumentException>(() => AskUserTool.Parse(Args($$"""{"questions":[{{Question("Q?", TwoOptions)}},{{Question("Q?", TwoOptions)}}]}""")));
        Assert.Throws<ToolArgumentException>(() => AskUserTool.Parse(Args(
            $$"""{"questions":[{{Question("Q?", """{"label":"A","description":"a"},{"label":"A","description":"b"}""")}}]}""")));
        var five = string.Join(",", Enumerable.Range(1, 5).Select(i => Question($"Q{i}?", TwoOptions)));
        Assert.Throws<ToolArgumentException>(() => AskUserTool.Parse(Args($$"""{"questions":[{{five}}]}""")));
    }

    [Fact]
    public void ValueOf_JoinsWithCommas_AndQuotesLabelsThatContainTheSeparator()
    {
        Assert.Equal("SQLite", UserAnswers.ValueOf(new UserAnswer("q", ["SQLite"])));
        Assert.Equal("Auth, \"Logs, metrics\"", UserAnswers.ValueOf(new UserAnswer("q", ["Auth", "Logs, metrics"])));
        Assert.Equal("my own words", UserAnswers.ValueOf(new UserAnswer("q", [], " my own words ")));
        Assert.Equal("Auth, extra", UserAnswers.ValueOf(new UserAnswer("q", ["Auth"], "extra")));
    }

    [Fact]
    public void ResultText_CoversEveryOutcome()
    {
        UserQuestion Q(string text) => new(text, "H", text, [new("A"), new("B")]);
        var questions = new[] { Q("Q1?"), Q("Q2?") };

        Assert.Equal("The user answered your questions: \"Q1?\"=\"A\", \"Q2?\"=(no answer). Continue with these answers in mind.",
            AskUserTool.ResultText(questions, new UserInputResult(UserInputOutcome.Answered, [new("Q1?", ["A"])])));
        Assert.StartsWith("The user answered: \"Q1?\"=\"A\", \"Q2?\"=\"typed\". Some answers are in the user's own words",
            AskUserTool.ResultText(questions, new UserInputResult(UserInputOutcome.Answered, [new("Q1?", ["A"]), new("Q2?", [], "typed")])));
        Assert.Equal(UserAnswers.DeclinedMessage, AskUserTool.ResultText(questions, UserInputResult.Declined));
    }

    [Fact]
    public void DetailsJson_RecordsStatusAndAnswers_AndMasksSecrets()
    {
        var questions = new[]
        {
            new UserQuestion("q1", "H1", "Q1?", [new("A", "a")]),
            new UserQuestion("q2", "H2", "Token?", [], IsSecret: true),
        };
        var pending = JsonDocument.Parse(UserAnswers.DetailsJson(questions, null)).RootElement;
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.False(pending.GetProperty("questions")[0].TryGetProperty("selected", out _));

        var answered = JsonDocument.Parse(UserAnswers.DetailsJson(questions,
            new UserInputResult(UserInputOutcome.Answered, [new("q1", ["A"]), new("q2", [], "s3cret")]))).RootElement;
        Assert.Equal("answered", answered.GetProperty("status").GetString());
        Assert.Equal("A", answered.GetProperty("questions")[0].GetProperty("selected")[0].GetString());
        Assert.Equal("••••••", answered.GetProperty("questions")[1].GetProperty("text").GetString());
        Assert.DoesNotContain("s3cret", UserAnswers.DetailsJson(questions, new UserInputResult(UserInputOutcome.Answered, [new("q2", [], "s3cret")])));
    }
}
