using System;

namespace BTAudioSink.Services;

/// <summary>
/// 一条三次贝塞尔缓动曲线，四个参数就是 CSS 里 cubic-bezier(x1, y1, x2, y2) 的数值。
/// 纵轴允许越界：大于 1 是「冲过头再回落」，小于 0 是「先往反方向缩一下」，
/// 弹窗开合的手感基本就靠这两下，单纯加长时长是换不来的。
/// </summary>
public readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    /// <summary>
    /// 给定时间进度 x（0..1），算出位移进度。
    /// 做法是先牛顿迭代反解「曲线在横轴上取到 x 时对应的参数 t」，再把同一个 t 代进纵轴。
    /// 横轴控制点必须落在 0..1 之间才有唯一解，纵轴不限制。
    /// </summary>
    public double At(double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;

        double cx = 3.0 * X1;
        double bx = 3.0 * (X2 - X1) - cx;
        double ax = 1.0 - cx - bx;

        double cy = 3.0 * Y1;
        double by = 3.0 * (Y2 - Y1) - cy;
        double ay = 1.0 - cy - by;

        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = ((ax * t + bx) * t + cx) * t - x;
            if (Math.Abs(error) < 1e-7) break;

            double slope = (3.0 * ax * t + 2.0 * bx) * t + cx;
            if (Math.Abs(slope) < 1e-10) break;

            t -= error / slope;
        }

        return ((ay * t + by) * t + cy) * t;
    }
}

/// <summary>
/// 托盘弹窗出入场的运动参数。
///
/// 出处是 DeskBox（WinUI 3）的托盘弹窗动画：位移逐帧推进、缓动统一用三次贝塞尔、
/// 弹出略过冲、收起先往回缩再加速离开。下面这四条曲线常数与它一一对应。
///
/// 有一条通道移植不了，写在这里是为了以后有人照着 DeskBox 补功能时少走弯路：
/// 它的 Mica 是自己合成进视觉树里的，所以材质本身能跟着淡、跟着缩放；
/// 我们的 Mica 由 DWM 直接画在窗口表面下方，透明度动不了它。
/// 所以淡入只作用在**内容层**（ContentLayer）：底下的材质一出现就是满的，
/// 文字和卡片淡上来。实测内容 opacity=0.5 时中心像素是 #8F1010（红与背板按 50% 混合），
/// 说明 WPF 内容确实带着 alpha 合成到 DWM 背板上——注意这要求窗口把客户区划成玻璃区
/// （GlassFrameThickness=-1），否则 DWM 不会铺材质，淡到底只剩黑。
/// 缩放通道同理放弃：窗口本身不能缩放，只缩内容会在固定的玻璃面板里留一圈空白。
/// </summary>
public static class FlyoutMotion
{
    /// <summary>弹出时长。DeskBox 的默认值就是 240ms。</summary>
    public const int ShowDurationMs = 240;

    /// <summary>收起时长。比弹出短一点，收回不该让人等。</summary>
    public const int HideDurationMs = 200;

    /// <summary>
    /// 弹出：起步就冲，越过终点约 5% 再落回来（DeskBox 的 Spring 曲线）。
    /// 实测峰值 1.097，48 DIP 的位移上大约过冲 4.6px —— 能感觉到「被弹出来」，又不至于晃眼。
    /// </summary>
    public static readonly CubicBezier ShowCurve = new(0.34, 1.56, 0.64, 1.0);

    /// <summary>
    /// 收起：先朝反方向缩一点点（实测最大 -9.7%，48 DIP 上约 4.7px）蓄力，再加速离开。
    /// 少了这一下，收起就是单纯地「滑走」，像被关掉而不是收起来。
    /// </summary>
    public static readonly CubicBezier HideCurve = new(0.6, -0.28, 0.735, 0.045);

    public static double EaseShow(double x) => ShowCurve.At(x);

    public static double EaseHide(double x) => HideCurve.At(x);

    /// <summary>
    /// 弹出时内容层的起始不透明度。用 0 而不是 0.3：从 0 淡上来才有「浮现」的意思，
    /// 从 0.3 起步看起来更像闪了一下。淡入比位移早结束——曲线在 50% 时已到 0.99，
    /// 剩下的时间只是位移在收尾，眼睛不会觉得内容还在慢慢显影。
    /// </summary>
    public const double ShowStartOpacity = 0.0;

    /// <summary>
    /// 弹出时的内容透明度。曲线越界大于 1 时夹回 1，不然 WPF 会把值当非法值忽略。
    /// </summary>
    public static double ContentOpacity(double progress, bool showing)
    {
        var eased = showing ? EaseShow(progress) : EaseHide(progress);
        var value = showing
            ? ShowStartOpacity + (1.0 - ShowStartOpacity) * eased
            : 1.0 - eased;

        return Math.Clamp(value, 0.0, 1.0);
    }
}
