using TCode.Config;
using TCode.Desktop.Controls.Composer;
using TCode.Desktop.Icons;
using TCode.Desktop.Localization;
using TCode.Desktop.ViewModels.Completion;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>提示词型内置命令（/init、/commit，docs/design/composer.md §9.3）：选中后插入 command tag，而不是立即执行。</summary>
public sealed class BuiltinPromptCommandTests
{
    [Theory]
    [InlineData("init")]
    [InlineData("commit")]
    public async Task PromptCommands_InsertCommandTag_AndShadowSameNamedCustomCommands(string name)
    {
        var executed = false;
        var provider = new BuiltinCommandProvider([
            new(name, TCodeLangResourceKind.BuiltinInitDescription, LucideKind.FileText, null),
            new("new", TCodeLangResourceKind.BuiltinNewDescription, LucideKind.SquarePen, () => executed = true),
        ]);

        var items = await provider.QueryAsync(name, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(new TagSegment(TagKind.Command, name, name), item.Action.Tag);
        Assert.Null(item.Action.Run);
        Assert.False(executed);
        Assert.True(BuiltinCommandProvider.Names.Contains(name));
    }
}
