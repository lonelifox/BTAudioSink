using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BTAudioSink.Interop;

/// <summary>
/// 让无边框窗口拿到 Win11 原生的圆角、阴影和 Mica 材质。
/// WPF 没有暴露这些属性，只能直接调 DWM。
/// </summary>
internal static class DwmHelper
{
    // Windows 11 SDK 里这几个枚举值没进 .NET 的托管封装，只能写字面量。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmcpRound = 2;
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 才有 Mica，Win10 调用会静默失败，提前挡掉省得窗口变透明。</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// 给窗口套上系统圆角。Win10 上调用会返回失败，窗口保持直角，不会报错。
    /// </summary>
    public static void ApplyRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var corner = DwmcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    /// <summary>
    /// 告诉 DWM 当前是深色还是浅色，窗口投影和边缘的明暗会跟着变。
    /// </summary>
    public static void ApplyColorMode(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>
    /// 开启 Mica（壁纸模糊材质）。返回 HRESULT，0 表示成功。
    /// 成功时调用方要把窗口背景改成透明，否则 WPF 自己画的不透明底色会把材质盖掉。
    /// </summary>
    public static int EnableMica(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return unchecked((int)0x80070006); // E_HANDLE

        var type = DwmsbtMainWindow;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int));
    }

    /// <summary>关闭 Mica，让窗口回到普通不透明窗口。</summary>
    public static void DisableMica(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var none = 0; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
    }

    /// <summary>
    /// 把 WPF 渲染层设成透明。
    /// 不设这一句，Mica 会被 WPF 自己的黑色底铺掉，窗口看起来是纯黑的。
    /// </summary>
    public static void MakeCompositionTransparent(Window window, bool transparent)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        if (source.CompositionTarget is null) return;

        source.CompositionTarget.BackgroundColor = transparent
            ? System.Windows.Media.Colors.Transparent
            : System.Windows.Media.Colors.Black;
    }
}
