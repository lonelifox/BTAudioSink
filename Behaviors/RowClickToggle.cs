using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BTAudioSink.Behaviors;

/// <summary>
/// 点列表里已经选中的那一行 = 取消选中。
///
/// 为什么要单独做：ListBox 在 SelectionMode=Single 下再点已选中的行不产生任何变化，
/// SelectionChanged 都不触发，于是绑在 IsSelected 上的展开块就收不回去。
/// 这是控件默认行为，不是动画没接上。
/// </summary>
public static class RowClickToggle
{
    public static readonly DependencyProperty CollapseSelectedOnClickProperty =
        DependencyProperty.RegisterAttached(
            "CollapseSelectedOnClick",
            typeof(bool),
            typeof(RowClickToggle),
            new PropertyMetadata(false, OnCollapseSelectedOnClickChanged));

    public static void SetCollapseSelectedOnClick(DependencyObject element, bool value) =>
        element.SetValue(CollapseSelectedOnClickProperty, value);

    public static bool GetCollapseSelectedOnClick(DependencyObject element) =>
        (bool)element.GetValue(CollapseSelectedOnClickProperty);

    private static void OnCollapseSelectedOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;

        // 先把旧的摘掉，避免同一个列表被挂两次。
        list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;

        if (e.NewValue is true)
        {
            list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
    }

    /// <summary>
    /// 在隧道阶段（Preview）拦一次，抢在 ListBoxItem 自己处理选中之前。
    /// </summary>
    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (!TryCollapse(list, e.OriginalSource as DependencyObject)) return;

        // 必须吃掉这次点击：否则 ListBoxItem 收到 MouseDown 会立刻把这一行重新选中，
        // 表现为「闪一下又展开」。
        e.Handled = true;
    }

    /// <summary>
    /// 判断这次点击该不该收起详情，该收就顺手清空选中并返回 true。
    /// 单独拆出来是为了能脱离鼠标输入直接测：WPF 的命中测试读的是真实光标，
    /// 投递消息、合成事件都到不了这里，只有直接调用才测得到。
    /// </summary>
    internal static bool TryCollapse(ListBox list, DependencyObject? source)
    {
        if (list.SelectedItem is null || source is null) return false;

        ListBoxItem? container = null;

        for (var node = source; node is not null; node = ParentOf(node))
        {
            // 行内按钮（自动 / 重连 / 连接断开）要用这次点击，别抢。
            if (node is ButtonBase) return false;

            if (node is ListBoxItem item)
            {
                container = item;
                break;
            }

            // 走到列表本身还没撞上行容器，说明点在行与行的空白上。
            if (node is ListBox) return false;
        }

        if (container is null) return false;
        if (!ReferenceEquals(container.DataContext, list.SelectedItem)) return false;

        list.SelectedItem = null;
        return true;
    }

    /// <summary>向上找父级。命中文本里的 Run 这类非 Visual 元素时得走逻辑树，否则会抛异常。</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
