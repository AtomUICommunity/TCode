using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 非图片附件不进会话：输入框显示文件名，发给 Agent 的是包着绝对路径的提示，不含文件内容。
/// </summary>
public sealed class AttachedFileTests
{
    [Fact]
    public void Prompt_WrapsAbsolutePath_AndDoesNotIncludeContents()
    {
        var path = Path.Combine(Path.GetTempPath(), "tkcode-attach-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "SECRET-CONTENTS");
        try
        {
            var tag = AttachedFiles.Tag(path);
            Assert.Equal(TagKind.AttachedFile, tag.Kind);
            Assert.Equal(Path.GetFullPath(path), tag.Value);
            Assert.Equal(Path.GetFileName(path), tag.Label);
            Assert.Equal(AttachedFiles.Reference(tag.Value), tag.ToPlainText());
            Assert.DoesNotContain("SECRET-CONTENTS", tag.ToPlainText());

            var draft = new Segment[] { new TextSegment("看一下 "), tag };
            var model = ComposerSegments.ModelText(draft);
            Assert.Equal("看一下 " + AttachedFiles.Reference(tag.Value), model);
            Assert.DoesNotContain("SECRET-CONTENTS", model);

            var sent = AttachedFiles.AppendInstruction(model);
            Assert.EndsWith(AttachedFiles.Instruction, sent);
            Assert.DoesNotContain("SECRET-CONTENTS", sent);
            Assert.Equal("看一下 [" + tag.Label + "]", AttachedFiles.Collapse(sent));
            Assert.Equal("看一下 [" + tag.Label + "]", ImagePrompt.Title(sent));
            Assert.DoesNotContain(tag.Value, ImagePrompt.Collapse(sent));

            var dto = ComposerSegments.ToDto(draft);
            Assert.NotNull(dto);
            Assert.Equal("attachedFile", dto![1].Kind);
            Assert.Equal(tag.Value, dto[1].Value);
            Assert.Equal(tag, ComposerSegments.FromDto(dto)[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LocalPath_FileIsAbsoluteAttachment_DirectoryStaysFolderTag()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-attach-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("tkcode-attach-out-").FullName;
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var log = Path.Combine(logs, "app.log");
            File.WriteAllText(log, "line");
            var sheet = Path.Combine(outside, "预算.xlsx");
            File.WriteAllText(sheet, "not-xlsx");

            var inside = Assert.IsType<TagSegment>(AttachedFiles.SegmentForLocalPath(log, root));
            Assert.Equal(TagKind.AttachedFile, inside.Kind);
            Assert.Equal(Path.GetFullPath(log), inside.Value);
            Assert.Equal("app.log", inside.Label);
            Assert.DoesNotContain("line", inside.ToPlainText());

            var external = Assert.IsType<TagSegment>(AttachedFiles.SegmentForLocalPath(sheet, root));
            Assert.Equal(TagKind.AttachedFile, external.Kind);
            Assert.Equal(Path.GetFullPath(sheet), external.Value);
            Assert.Contains(Path.GetFullPath(sheet), external.ToPlainText());

            var folder = Assert.IsType<TagSegment>(AttachedFiles.SegmentForLocalPath(logs, root));
            Assert.Equal(TagKind.Folder, folder.Kind);
            Assert.Equal("logs", folder.Value);

            var outsideDir = Assert.IsType<TextSegment>(AttachedFiles.SegmentForLocalPath(outside, root));
            Assert.Contains(Path.GetFullPath(outside), outsideDir.Text);

            // @ 提及仍然是相对路径，不走附件提示
            Assert.Equal("@src/a.cs", new TagSegment(TagKind.File, "src/a.cs", "a.cs").ToPlainText());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
