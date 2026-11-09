using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace BTAudioSink.Behaviors;

/// <summary>
/// 给一块区域加上「展开 / 收起」过渡：高度从 0 撑到内容实际高度，同时淡入。
///
/// 为什么不用 Visibility 直接切：WPF 的可见性变化是瞬时的，列表行会突然长高一块，
/// 弹窗本身是 SizeToContent，窗口会跟着跳一下，看起来像闪了一下。
///
/// 为什么不给 Height 做动画：Height 是 Auto 时动画会被布局忽略，
/// 必须先量出真实高度再写死，而详情内容会随数据变长，写死就会截断。
/// 这里动的是 MaxHeight——它从小往大走，内容按真实高度排，涨到目标值就停住，
/// 既动画得出来，又不需要提前知道最终高度。
/// </summary>
public static class RevealBehavior
{
    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(190));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(140));

    public static readonly DependencyProperty IsRevealedProperty =
        DependencyProperty.RegisterAttached(
            "IsRevealed",
            typeof(bool),
            typeof(RevealBehavior),
            new PropertyMetadata(false, OnIsRevealedChanged));

    public static void SetIsRevealed(DependencyObject element, bool value) =>
        element.SetValue(IsRevealedProperty, value);

    public static bool GetIsRevealed(DependencyObject element) =>
        (bool)element.GetValue(IsRevealedProperty);

    private static void OnIsRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true) Expand(element);
        else Collapse(element);
    }

    /// <summary>展开：量出内容高度，再把它从当前高度一口气撑上去。</summary>
    private static void Expand(FrameworkElement element)
    {
        // 中途被打断（收起一半又点开）时，从上一次停下的位置继续，不要从 0 重来。
        var from = element.Visibility == Visibility.Visible ? element.MaxHeight : 0;
        if (double.IsNaN(from) || double.IsInfinity(from)) from = 0;

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        element.MaxHeight = from;
        element.Visibility = Visibility.Visible;

        var target = MeasureNaturalHeight(element);

        if (target <= 0)
        {
            // 量不出来（还没进入可视树、宽度为 0）就不要动画，直接显示，
            // 绝不能让内容卡在高度 0 里看不见。
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.MaxHeight = double.PositiveInfinity;
            element.Opacity = 1;
            return;
        }

        var height = new DoubleAnimation(from, target, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        // 动画结束后交还控制权：MaxHeight 设回无限，详情文字换行、字号变化都不会被裁掉。
        height.Completed += (_, _) =>
        {
            if (!GetIsRevealed(element)) return;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = double.PositiveInfinity;
        };

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, height);

        // 淡入：从收起态开始时才需要，中途被打断时保持当前透明度接着淡。
        var startOpacity = from <= 0 ? 0 : element.Opacity;
        if (from <= 0) element.Opacity = 0;
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(startOpacity, 1, new Duration(TimeSpan.FromMilliseconds(130))));
    }

    /// <summary>
    /// 量出内容撑满后的自然高度。
    ///
    /// 注意<b>不能</b>在这里调 <c>element.UpdateLayout()</c>：本方法是从依赖属性变更回调里进来的，
    /// 此刻可能正处在布局过程中，强制同步布局会抛异常，整个程序直接崩掉（实测进程静默退出）。
    /// 所以改成单独量内部内容，再把边框和内边距加回去——这个操作在布局任何阶段都是安全的。
    /// </summary>
    private static double MeasureNaturalHeight(FrameworkElement element)
    {
        if (element is Border border && border.Child is FrameworkElement child)
        {
            var frame = border.BorderThickness;
            var padding = border.Padding;

            // 折叠状态下元素自己不参与排列，ActualWidth 是 0，只能向父级要宽度。
            // 用父级宽度量出来的结果和 expands 之后完全一致——这一行的宽度不会因为展开而变化。
            var width = element.ActualWidth;
            if (width <= 0 && element.Parent is FrameworkElement parent) width = parent.ActualWidth;

            var available = width - frame.Left - frame.Right - padding.Left - padding.Right;
            if (available <= 0) available = double.PositiveInfinity;

            child.Measure(new Size(available, double.PositiveInfinity));
            return child.DesiredSize.Height + padding.Top + padding.Bottom + frame.Top + frame.Bottom;
        }

        return element.ActualHeight;
    }

    /// <summary>收起：高度压回 0，同时淡出，结束后才真正 Collapsed 把布局位置让出来。</summary>
    private static void Collapse(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible) return;

        var from = element.ActualHeight;
        if (from <= 0 || double.IsNaN(from))
        {
            HardHide(element);
            return;
        }

        var height = new DoubleAnimation(from, 0, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };

        height.Completed += (_, _) =>
        {
            // 中途又被点开的话，交给 Expand 处理，别在这里把 Visible 关掉。
            if (GetIsRevealed(element)) return;
            HardHide(element);
        };

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, height);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(element.Opacity, 0, CollapseDuration));
    }

    /// <summary>把元素彻底收起来，并把动画留下的值清干净（否则下次展开会从旧值开始）。</summary>
    private static void HardHide(FrameworkElement element)
    {
        element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);

        element.MaxHeight = 0;
        element.Opacity = 1;
        element.Visibility = Visibility.Collapsed;
    }
}
