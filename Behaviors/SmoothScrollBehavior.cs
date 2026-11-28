using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BTAudioSink.Behaviors;

/// <summary>
/// 让 ScrollViewer 的滚轮滚动变顺滑。
///
/// <para><b>为什么需要</b>：WPF 默认的滚轮是一格一跳（约 48px，无过渡），
/// 在托盘弹窗这种小面板里看得很生硬，触控板更是难受。这里把滚轮事件接管下来，
/// 只记录一个「目标位置」，再按帧向它逼近——手感接近系统设置页。</para>
///
/// <para><b>为什么不用 ScrollViewer 自带的平滑</b>：WPF 没有。惯性只对触屏/触控板的
/// 平移（<c>PanningMode</c>）生效，鼠标滚轮永远是离散跳变。</para>
///
/// <para><b>实现要点</b>：用 <see cref="CompositionTarget.Rendering"/> 逐帧推进，
/// 与显示器刷新同频（165Hz 屏就是 165fps），不需要 DispatcherTimer 也能跟上。
/// 每帧走剩余距离的固定比例，形成先快后慢的缓出。</para>
/// </summary>
public static class SmoothScrollBehavior
{
    /// <summary>一次滚轮格对应的滚动距离（DIP）。约 1.6 行，比系统默认略多一点。</summary>
    private const double WheelStep = 72;

    /// <summary>每帧逼近剩余距离的比例。0.22 大约 8~10 帧走完，约 60~120ms。</summary>
    private const double EaseFactor = 0.22;

    /// <summary>小于这个距离就直接落位，避免无限逼近。</summary>
    private const double StopThreshold = 0.4;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    /// <summary>每个 ScrollViewer 一份状态，避免多个列表互相干扰。</summary>
    private sealed class State
    {
        public double Target;
        public bool Animating;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(SmoothScrollBehavior));

    /// <summary>当前正在动画的 ScrollViewer。只遍历这几个，避免每帧扫整棵可视树。</summary>
    private static readonly List<ScrollViewer> Active = new();

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        if ((bool)e.NewValue)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.Unloaded += OnUnloaded;
            viewer.Loaded += OnLoaded;
        }
        else
        {
            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            viewer.Unloaded -= OnUnloaded;
            viewer.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer) viewer.SetValue(StateProperty, new State());
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer) viewer.ClearValue(StateProperty);
        CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (viewer.ScrollableHeight <= 0) return;   // 内容没超出，交给外面处理

        e.Handled = true;

        if (viewer.GetValue(StateProperty) is not State state)
        {
            state = new State { Target = viewer.VerticalOffset };
            viewer.SetValue(StateProperty, state);
        }

        // 正在动的时候以「目标」为基准累加，连滚多格才不会卡住。
        state.Target = Clamp(state.Target - e.Delta / 120.0 * WheelStep,
            0, viewer.ScrollableHeight);

        if (!state.Animating)
        {
            state.Animating = true;
            if (!Active.Contains(viewer))
            {
                Active.Add(viewer);
                if (Active.Count == 1) CompositionTarget.Rendering += OnRendering;
            }
        }
    }

    /// <summary>逐帧逼近目标位置。只处理 <see cref="Active"/> 里的那几个。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var viewer = Active[i];
            if (viewer.GetValue(StateProperty) is not State state)
            {
                Active.RemoveAt(i);
                continue;
            }

            double current = viewer.VerticalOffset;
            double diff = state.Target - current;

            if (Math.Abs(diff) < StopThreshold)
            {
                viewer.ScrollToVerticalOffset(state.Target);
                state.Animating = false;
                Active.RemoveAt(i);
                continue;
            }

            viewer.ScrollToVerticalOffset(current + diff * EaseFactor);
        }

        if (Active.Count == 0) CompositionTarget.Rendering -= OnRendering;
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
