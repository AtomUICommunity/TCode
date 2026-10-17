using Avalonia.Controls;
using Avalonia.Input;

namespace TCode.Desktop.Controls;

/// <summary>
/// 列分割条：前一列折叠（宽 0）时，改为拿左侧最近的可见列与后一列互换宽度。
/// GridSplitter 只调整紧邻的两列，折叠列无法再缩小，拖动就被锁死（编辑区关闭时文件树分割条即是如此）。
/// </summary>
public class PanelSplitter : GridSplitter
{
    protected override Type StyleKeyOverride => typeof(GridSplitter);

    private ColumnDefinition? _previous;
    private ColumnDefinition? _next;

    protected override void OnDragStarted(VectorEventArgs e)
    {
        _previous = _next = null;
        if (Parent is Grid grid && ResizeDirection == GridResizeDirection.Columns)
        {
            var columns = grid.ColumnDefinitions;
            var index = Grid.GetColumn(this);
            if (index > 0 && index + 1 < columns.Count && columns[index - 1].ActualWidth <= 0)
            {
                for (var i = index - 2; i >= 0; i--)
                {
                    if (columns[i].ActualWidth > 0)
                    {
                        _previous = columns[i];
                        break;
                    }
                }
                _next = _previous is null ? null : columns[index + 1];
            }
        }
        if (_previous is null)
        {
            base.OnDragStarted(e);
        }
    }

    protected override void OnDragDelta(VectorEventArgs e)
    {
        if (_previous is null || _next is null)
        {
            base.OnDragDelta(e);
            return;
        }
        // 只改后一列的像素宽度；左侧列若是 *，自然吃掉差值。增量受两列各自的最小 / 最大宽度约束
        var width = _next.ActualWidth;
        var min = Math.Max(-(_previous.ActualWidth - _previous.MinWidth), width - _next.MaxWidth);
        var max = width - _next.MinWidth;
        var delta = Math.Clamp(e.Vector.X, Math.Min(min, 0), Math.Max(max, 0));
        _next.Width = new GridLength(width - delta);
        e.Handled = true;
    }

    protected override void OnDragCompleted(VectorEventArgs e)
    {
        if (_previous is null)
        {
            base.OnDragCompleted(e);
        }
        _previous = _next = null;
    }
}
