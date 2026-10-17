using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace TCode.Desktop.Views;

public partial class DiffView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<DiffView, IEnumerable?>(nameof(Lines));

    /// <summary>可见区域的最大高度；0 表示不限（占满父容器，变更面板使用）。</summary>
    public static readonly StyledProperty<double> MaxContentHeightProperty =
        AvaloniaProperty.Register<DiffView, double>(nameof(MaxContentHeight), 360);

    static DiffView()
    {
        LinesProperty.Changed.AddClassHandler<DiffView>((view, _) => view.Rows.ItemsSource = view.Lines);
        MaxContentHeightProperty.Changed.AddClassHandler<DiffView>((view, e) => view.Scroller.MaxHeight = (double)e.NewValue! > 0 ? (double)e.NewValue! : double.PositiveInfinity);
    }

    public DiffView()
    {
        InitializeComponent();
    }

    public IEnumerable? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public double MaxContentHeight
    {
        get => GetValue(MaxContentHeightProperty);
        set => SetValue(MaxContentHeightProperty, value);
    }
}
