using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TCode.Desktop.ViewModels;

/// <summary>Agent 运行中发送、排队等待的一条消息：可编辑、移除，或立即插入当前轮。</summary>
public sealed partial class QueuedPromptViewModel(string text, IReadOnlyList<ImageAttachmentViewModel> images, SessionViewModel session)
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

    /// <summary>显示用：图片引用折叠为「[图片 #N]」。</summary>
    public string DisplayText => ImagePrompt.Collapse(Text);

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
            Text = text;
        }
    }

    [RelayCommand]
    private void Remove() => Session.QueuedPrompts.Remove(this);

    [RelayCommand]
    private Task SendNowAsync() => Owner?.SendQueuedNowAsync(this) ?? Task.CompletedTask;
}
