using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 行内编辑（TextBox 附加行为）：变为可见时获得焦点并全选；Enter 提交，Esc 取消，失去焦点提交。
/// </summary>
public static class InlineEdit
{
    public static readonly AttachedProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("CommitCommand", typeof(InlineEdit));

    public static readonly AttachedProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("CancelCommand", typeof(InlineEdit));

    static InlineEdit()
    {
        CommitCommandProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.OldValue is null && e.NewValue is not null)
            {
                box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                box.LostFocus += OnLostFocus;
                box.PropertyChanged += OnPropertyChanged;
            }
        });
    }

    public static ICommand? GetCommitCommand(TextBox box) => box.GetValue(CommitCommandProperty);
    public static void SetCommitCommand(TextBox box, ICommand? value) => box.SetValue(CommitCommandProperty, value);
    public static ICommand? GetCancelCommand(TextBox box) => box.GetValue(CancelCommandProperty);
    public static void SetCancelCommand(TextBox box, ICommand? value) => box.SetValue(CancelCommandProperty, value);

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && e.Property == Visual.IsVisibleProperty && e.NewValue is true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            Execute(GetCommitCommand(box));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Execute(GetCancelCommand(box));
            e.Handled = true;
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Execute(GetCommitCommand(box));
        }
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
