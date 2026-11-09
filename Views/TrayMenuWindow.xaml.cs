using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using BTAudioSink.Interop;
using BTAudioSink.Services;
using Forms = System.Windows.Forms;

namespace BTAudioSink.Views;

/// <summary>
/// 托盘右键菜单。
///
/// 不用 WinForms 的 ContextMenuStrip：那是系统绘制的方角灰菜单，字号、圆角、
/// 悬停高亮都跟主界面不是一套，深色模式下尤其突兀。这里用无边框透明窗口自绘
/// 圆角卡片，配色全部走 DynamicResource，所以切换深浅色时菜单会即时跟着变。
///
/// 关闭时机靠 Deactivated（点到菜单外面，窗口失去激活）→ 必须先抢到前台，
/// 否则它永远收不到 Deactivated，会一直挂在屏幕上。
/// </summary>
public partial class TrayMenuWindow : Window
{
    private readonly App _app;
    private bool _loading;

    /// <summary>已经在关的过程中。Close 是不可重入的。</summary>
    private bool _closing;

    public TrayMenuWindow(App app)
    {
        _app = app;
        InitializeComponent();

        // 窗口本身没有标题栏（WindowStyle=None），但无障碍和任务管理器会读 Title，
        // 别让它在英文界面下还挂个中文标题。
        Title = Loc.Get("Tray.MenuTitle");

        LoadState();

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dismiss();
        };
    }

    /// <summary>
    /// 幂等的关闭。菜单有四条关闭路径（失活、Esc、点菜单项、App.ExitApp），
    /// 它们会互相踩：点「退出」时先 Close，Close 过程中窗口失去激活又触发
    /// Deactivated → 再 Close 一次，WPF 直接抛 InvalidOperationException
    /// （「在窗口关闭期间，无法将可见性设置为可见…」）。
    /// 这个异常是从 WndProc 里冒出来的，会把 App.ExitApp 打断在 Shutdown() 之前；
    /// 而 ShutdownMode 是 OnExplicitShutdown，于是进程既不退出也没有窗口——
    /// 表现为任务管理器里一个「未响应」的 BTAudioSink。
    /// </summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;

        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 窗口已经在系统那边关掉了（Deactivated 与 Close 竞态），不用管。
        }
    }

    private void LoadState()
    {
        _loading = true;
        try
        {
            ThemeAuto.IsChecked = _app.Settings.Theme == AppTheme.Auto;
            ThemeLight.IsChecked = _app.Settings.Theme == AppTheme.Light;
            ThemeDark.IsChecked = _app.Settings.Theme == AppTheme.Dark;

            MicaToggle.IsChecked = _app.Settings.UseMica;
            // Win10 没有 Mica，置灰比点了没反应更诚实。
            MicaToggle.IsEnabled = DwmHelper.IsWindows11;
            MicaToggle.ToolTip = DwmHelper.IsWindows11
                ? Loc.Get("Tray.MicaTip")
                : Loc.Get("Tray.MicaUnsupported");

            StartupToggle.IsChecked = _app.Settings.StartWithWindows;

            // 语言：单选钮的选中态就是当前语言。设值会触发 Checked，
            // 所以上面那圈 _loading 必须已经生效——它挡住了这次「假切换」。
            LanguageChinese.IsChecked = Loc.Language == AppLanguage.Chinese;
            LanguageEnglish.IsChecked = Loc.Language == AppLanguage.English;

            DisconnectAllItem.IsEnabled = _app.ConnectedCount > 0;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>在鼠标位置弹出：贴着光标，靠屏幕边缘时自动翻到另一侧。</summary>
    public void ShowAtCursor()
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        var scale = GetScale();

        // 先显示出来才知道内容多大（SizeToContent）
        Show();
        UpdateLayout();

        double width = ActualWidth;
        double height = ActualHeight;

        double left = cursor.X / scale;
        double top = cursor.Y / scale;
        double areaLeft = area.Left / scale;
        double areaTop = area.Top / scale;
        double areaRight = area.Right / scale;
        double areaBottom = area.Bottom / scale;

        // 光标在下半屏 → 菜单往上展开；在右半屏 → 右边缘对齐光标。托盘在右下角时正好是「向左上弹」。
        if (cursor.Y > area.Top + area.Height / 2.0) top -= height;
        if (cursor.X > area.Left + area.Width / 2.0) left -= width;

        Left = Math.Max(areaLeft, Math.Min(left, areaRight - width));
        Top = Math.Max(areaTop, Math.Min(top, areaBottom - height));

        ForceForeground();
        Focus();
    }

    private static double GetScale()
    {
        try
        {
            using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            var scale = graphics.DpiX / 96.0;
            return scale > 0.1 ? scale : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 托盘图标被右键时，本进程并不是「拿到鼠标输入」的那个，单纯 Activate() 可能不生效，
    /// 窗口没激活就永远收不到 Deactivated。显式抢一次前台。
    /// </summary>
    private void ForceForeground()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetForegroundWindow(handle);
        Activate();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---------- 菜单项 ----------

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _app.ShowMainWindow();
    }

    private void OnDisconnectAllClick(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _app.DisconnectAllDevices();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        // 不在这里 Close，交给 ExitApp 统一关：避免「这里关一次、那里关一次」，
        // 也避免关菜单的时候退出流程还没开始跑。
        _app.ExitApp();
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        var mode = tag switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Auto,
        };

        _app.SetThemeMode(mode);
        _app.AppendThemeLog(mode);
    }

    private void OnMicaChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetMicaEnabled(MicaToggle.IsChecked == true);
    }

    /// <summary>
    /// 切换界面语言。换完不用关菜单：菜单里的文字走 DynamicResource，
    /// 资源字典一换就跟着变了，菜单会当场变成另一种语言。
    /// </summary>
    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        var language = tag switch
        {
            "English" => AppLanguage.English,
            _ => AppLanguage.Chinese,
        };

        if (language == Loc.Language) return;

        _app.SetLanguage(language);
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = StartupToggle.IsChecked == true;
        var actual = _app.SetStartupEnabled(wanted);

        // 注册表没写进去（组策略 / 安全软件拦了），把开关拨回真实状态，别骗人。
        if (actual != wanted)
        {
            _loading = true;
            StartupToggle.IsChecked = actual;
            _loading = false;
        }
    }
}
