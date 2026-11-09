using System;

namespace BTAudioSink.Services;

/// <summary>任务栏所在的屏幕边。自动隐藏的任务栏不占工作区，按底部处理。</summary>
public enum TrayEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>屏幕矩形，单位统一用 DIP（WPF 的 Left/Top 坐标系）。</summary>
public readonly record struct ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

public readonly record struct FlyoutPoint(double Left, double Top);

/// <summary>
/// 弹窗定位的纯计算：给定任务栏在哪条边、窗口多大，算出该摆在哪、往哪个方向滑。
/// 单独拆出来是为了能脱离窗口直接测——任务栏换边这种人肉验证成本太高。
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>弹窗与工作区边缘的间距。</summary>
    public const double Gap = 12;

    /// <summary>滑入/滑出的位移。太大像飞，太小看不出从任务栏里出来。</summary>
    public const double FlyDistance = 36;

    /// <summary>
    /// 工作区相对整屏缩进最多的那条边就是任务栏。
    /// 四条边都是 0（任务栏自动隐藏）时按底部处理，这也是绝大多数人的配置。
    /// </summary>
    public static TrayEdge EdgeOf(ScreenRect bounds, ScreenRect workArea)
    {
        var left = workArea.Left - bounds.Left;
        var top = workArea.Top - bounds.Top;
        var right = bounds.Right - workArea.Right;
        var bottom = bounds.Bottom - workArea.Bottom;

        var max = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (max <= 0) return TrayEdge.Bottom;

        if (max == left) return TrayEdge.Left;
        if (max == right) return TrayEdge.Right;
        if (max == top) return TrayEdge.Top;
        return TrayEdge.Bottom;
    }

    /// <summary>
    /// 贴托盘那一头摆放，同时整个窗口留在工作区里：
    /// 底部/右侧任务栏贴右下，顶部任务栏贴右上，左侧任务栏贴左下
    /// （左侧任务栏的通知区在它自己的末端，也就是屏幕左下角）。
    /// </summary>
    public static FlyoutPoint Place(TrayEdge edge, ScreenRect workArea, double width, double height)
    {
        double left, top;

        switch (edge)
        {
            case TrayEdge.Top:
                left = workArea.Right - width - Gap;
                top = workArea.Top + Gap;
                break;
            case TrayEdge.Left:
                left = workArea.Left + Gap;
                top = workArea.Bottom - height - Gap;
                break;
            default:
                left = workArea.Right - width - Gap;
                top = workArea.Bottom - height - Gap;
                break;
        }

        // 窗口比工作区还高时别顶出屏幕外。
        left = Math.Max(workArea.Left, left);
        top = Math.Max(workArea.Top, top);
        return new FlyoutPoint(left, top);
    }

    /// <summary>
    /// 滑入的起点相对目标位置的偏移：从任务栏里钻出来的方向。
    /// 底部任务栏 → 起点低 26px；左侧任务栏 → 起点左 26px。
    /// </summary>
    public static (double X, double Y) EnterOffset(TrayEdge edge) => edge switch
    {
        TrayEdge.Bottom => (0, FlyDistance),
        TrayEdge.Top => (0, -FlyDistance),
        TrayEdge.Left => (-FlyDistance, 0),
        _ => (FlyDistance, 0),
    };
}
