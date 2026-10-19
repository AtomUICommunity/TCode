using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 工具 Details / 参数的解析不能因为形状不对而抛异常：这些解析在 UI 线程的会话事件里执行，未处理异常会直接终止进程
/// （0.1.2：Claude Code 出错时的 tool_use_result 是字符串，被原样转发为 Details）。
/// </summary>
public sealed class ToolDetailsParseTests
{
    [Theory]
    [InlineData("\"Error: File has not been read yet. Read it first before writing to it.\"")]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Parse_NonObjectRoot_ReturnsNull(string json)
    {
        Assert.Null(ParsedToolDetails.Parse(json));
    }

    [Fact]
    public void Parse_WrongFieldTypes_FallsBackToDefaults()
    {
        var subagent = ParsedToolDetails.Parse("""{"kind":"subagent","name":7,"toolUses":"3","tokens":"many"}""");
        Assert.NotNull(subagent);
        Assert.Null(subagent.SubagentName);
        Assert.Equal(0, subagent.ToolUses);
        Assert.Equal(0, subagent.Tokens);

        var tasks = ParsedToolDetails.Parse("""{"kind":"tasks","tasks":["a",{"id":"1","subject":"s","status":"completed"},3]}""");
        Assert.NotNull(tasks?.Tasks);
        Assert.Equal(3, tasks.Tasks.Count);
        Assert.Equal("s", tasks.Tasks[1].Subject);
    }

    [Fact]
    public void QuestionCard_NonObjectQuestions_DoesNotThrow()
    {
        var parsed = QuestionCardViewModel.Parse("""{"questions":["Which one?",{"question":"Q","header":"H","selected":"x"}]}""", details: false);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Value.Lines.Count);
        Assert.Equal("Q", parsed.Value.Lines[1].Question);
    }

    [Fact]
    public void PrimaryArgument_NonStringChangePath_DoesNotThrow()
    {
        Assert.Equal("b.txt", ToolPresentation.PrimaryArgument(ToolCategory.Edit, """{"changes":[{"path":1},{"path":"b.txt"}]}"""));
    }
}
