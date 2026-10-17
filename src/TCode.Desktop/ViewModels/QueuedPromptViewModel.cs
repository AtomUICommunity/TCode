using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>Agent 运行中发送、排队等待的一条消息：可编辑、移除，或立即插入当前轮。</summary>
/// <param name="segments">输入框里的样子（含 tag，发送时 Host 据此展开 command）；没有 tag 时为 null。</param>
public sealed partial class QueuedPromptViewModel(string text, IReadOnlyList<ImageAttachmentViewModel> images, SessionViewModel session,
    IReadOnlyList<PromptSegmentDto>? segments = null)
    : ObservableObject
{
    public QueuedPromptViewModel(string text, SessionViewModel session) : this(text, [], session)
    {
    }

    public SessionViewModel Session { get; } = session;

    /// <summary>发送的文本（图片占位符已展开为带路径的引用）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string Text { get; set; } = text;

    /// <summary>输入框的片段；编辑文字后只保留其中的 command tag。</summary>
    public IReadOnlyList<PromptSegmentDto>? Segments { get; private set; } = segments;

    /// <summary>显示用：command 显示为 /名称，图片引用折叠为「[图片 #N]」。</summary>
    public string DisplayText => ImagePrompt.Collapse(CommandPrefix + Text);

    private string CommandPrefix => string.Concat((Segments ?? []).Where(s => s.Kind == "command").Select(s => $"/{s.Value} "));

    public IReadOnlyList<ImageAttachmentViewModel> Images { get; } = images;

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    /// <summary>执行“立即发送”的会话控制器（由 <see cref="ConversationViewModel"/> 设置）。</summary>
    internal ConversationViewModel? Owner { get; set; }

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void CommitEdit()
    {
        if (!IsEditing)
        {
            return;
        }
        IsEditing = false;
        var text = EditText.Trim();
        if (text.Length == 0)
        {
            Session.QueuedPrompts.Remove(this);
        }
        else
        {
            // 编辑的是文字部分：command tag 保留，其余 tag 变成编辑后的纯文字
            var commands = (Segments ?? []).Where(s => s.Kind == "command").ToList();
            Segments = commands.Count > 0 ? [.. commands, new PromptSegmentDto("text", " " + text)] : null;
            Text = text;
        }
    }

    [RelayCommand]
    private void Remove() => Session.QueuedPrompts.Remove(this);

    [RelayCommand]
    private Task SendNowAsync() => Owner?.SendQueuedNowAsync(this) ?? Task.CompletedTask;
}
