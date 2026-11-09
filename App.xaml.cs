using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using BTAudioSink.Services;
using BTAudioSink.Views;
using Forms = System.Windows.Forms;

namespace BTAudioSink;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private Forms.NotifyIcon? _tray;
    private TrayMenuWindow? _trayMenu;
    private MainWindow? _main;
    private bool _lightTheme;
    private bool _exiting;

    /// <summary>
    /// 本次进程是 `--exit` 助手（只负责通知别人退出），不是真正的实例。
    /// 这类进程从没启用过 Mica，退出时不能去擦 MicaGuard 的标记——
    /// 擦掉之后如果运行中的实例接着异常死亡，下次启动就检测不到，
    /// 该关的毛玻璃没关，窗口可能整片变黑。
    /// </summary>
    private bool _exitHelper;

    /// <summary>
    /// 退出信号。命名事件，跨进程可见。
    /// 卸载程序或脚本用「再启动一个 BTAudioSink.exe --exit」的方式让它退出，
    /// 比按进程名强杀干净——强杀会跳过 MicaGuard 的标记清理，
    /// 下次启动会误判成「上次异常退出」而自动关掉毛玻璃。
    /// </summary>
    private const string ExitEventName = @"Local\BTAudioSink.ExitRequest";

    private EventWaitHandle? _exitEvent;

    /// <summary>
    /// 当前铺在资源字典里的语言字典。切语言时整体换掉它，
    /// WPF 会在字典被替换时重新解析所有 DynamicResource 绑定。
    /// </summary>
    private ResourceDictionary? _languageDictionary;

    public AppSettings Settings { get; private set; } = new();

    public bool IsLightTheme => _lightTheme;

    /// <summary>当前已连接的设备台数，托盘菜单用来决定「全部断开」是否可用。</summary>
    public int ConnectedCount { get; private set; }

    /// <summary>上次启用毛玻璃后程序异常结束，本次已自动关闭 Mica。</summary>
    public bool MicaAutoDisabled { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 记完日志就让程序继续跑。这程序是托盘常驻的，一旦闪退，
        // 用户看到的只是「它自己消失了」，很容易误判成蓝牙或系统的问题，
        // 而且托盘图标没了就再也点不回来。
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);

        // --exit：不是启动程序，是让已经在跑的那个退出。
        // 卸载器用它来关掉托盘常驻的进程（卸载时文件被占用会删不掉）。
        // 必须放在单实例判断之前：这个进程自己不是实例，得去通知真正的实例。
        if (Array.Exists(e.Args, arg => arg == "--exit"))
        {
            _exitHelper = true;
            // 退出码要如实反映结果：卸载器靠它判断「实例是不是真的退干净了」，
            // 退不掉却继续删文件，只会以「文件被占用」失败收场。
            Shutdown(RequestRunningInstanceExit() ? 0 : 2);
            return;
        }

        _mutex = new Mutex(true, @"Local\BTAudioSink.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(Loc.Get("Msg.AlreadyRunning"), "BTAudioSink",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 监听退出信号。已经有别人打开过这个事件句柄时，
        // 这里要传 createdNew=false 才能拿到同一个内核对象。
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName, out _);

        Settings = AppSettings.Load();
        // 语言要先于界面加载生效：资源字典是窗口创建前就铺好的。
        ApplyLanguage(Settings.Language, save: false);
        // 以注册表实际状态为准，避免用户在任务管理器里删了自启项后界面还显示勾选。
        Settings.StartWithWindows = StartupManager.IsEnabled();

        // 上次开 Mica 后没正常退出，多半是材质没渲染出来导致窗口全黑。
        // 先关掉，让用户至少能看到界面。
        if (MicaGuard.PreviousRunAbnormal)
        {
            MicaGuard.Clear();
            if (Settings.UseMica)
            {
                Settings.UseMica = false;
                Settings.Save();
                MicaAutoDisabled = true;
            }
        }

        _lightTheme = ResolveLightTheme();
        ApplyTheme(_lightTheme);

        // 跟随系统时要能感知用户在设置里切换明暗，否则「自动」名不副实。
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // 关机 / 注销。主窗口的 OnClosing 对一切关闭请求都 e.Cancel = true
        // （弹窗本来就没有关闭按钮），系统结束会话时走的正是这条路——
        // 窗口拒绝关闭，进程被系统直接终止，MicaGuard.Clear() 根本没机会执行，
        // mica.lock 就留在磁盘上。后果是用户每次关机重启，毛玻璃都会被当成
        // 「上次异常退出」自动关掉，而且日志里查不到原因。
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        CreateTray();

        _main = new MainWindow(this);
        _main.FlyoutHidden += (_, _) => _flyoutHiddenAt = DateTime.UtcNow;

        var startMinimized = Array.Exists(e.Args, arg => arg == "--minimized");
        if (startMinimized) _main.Hide();
        else ShowMainWindow();

        StartExitListener();
    }

    /// <summary>
    /// 让正在运行的实例退出，并等它真的退干净。
    /// 调用方（如卸载器）可以同步等这个返回，返回时 exe 已经不被占用了。
    ///
    /// 等待靠单实例互斥体：运行中的实例持有它，退出时释放。
    /// 这里等的就是这个释放。
    /// </summary>
    private bool RequestRunningInstanceExit()
    {
        try
        {
            // 已存在的事件句柄才能 Set；没人开过就是没人在跑。
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
            else
            {
                return true;
            }
        }
        catch
        {
            // 通知失败不算错误，下面靠互斥体判断真实状态。
        }

        // 等运行中的实例把互斥体放掉。等不到就说明它没在跑（或退不掉），
        // 返回 false 让调用方决定要不要强杀。
        try
        {
            using var mutex = Mutex.OpenExisting(@"Local\BTAudioSink.SingleInstance");
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            // 对方被强杀，互斥体随进程终止而释放——这正是「已经退干净」。
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 没有实例在跑，已经退干净了。
            return true;
        }
        catch
        {
            // 等不到就当没退干净，交给调用方处理。
            return false;
        }
    }

    /// <summary>
    /// 后台线程等退出信号，收到就切回 UI 线程走正常退出流程。
    /// 走正常流程是关键：ExitApp 会清 MicaGuard 标记、断开蓝牙、写日志。
    /// </summary>
    private void StartExitListener()
    {
        if (_exitEvent is null) return;

        var thread = new Thread(() =>
        {
            try
            {
                while (_exitEvent.WaitOne())
                {
                    // 切回 UI 线程执行退出，不能在工作线程上碰 WPF 对象。
                    Dispatcher.Invoke(ExitApp);
                    return;
                }
            }
            catch
            {
                // 监听线程的异常不影响主流程。
            }
        })
        {
            IsBackground = true,
            Name = "BTAudioSink.ExitListener"
        };
        thread.Start();
    }

    /// <summary>按任务栏方位把弹窗贴出来，并播入场动画。</summary>
    public void ShowMainWindow() => _main?.ShowAtTray();

    /// <summary>切换外观模式并保存。Auto 会让主题重新跟随系统。</summary>
    public void SetThemeMode(AppTheme mode)
    {
        Settings.Theme = mode;
        Settings.Save();
        ApplyThemeMode();
    }

    /// <summary>按当前设置重新解析并应用主题。托盘菜单里的选中态由菜单自己从 Settings 读。</summary>
    public void ApplyThemeMode()
    {
        var light = ResolveLightTheme();
        var changed = light != _lightTheme;
        _lightTheme = light;

        if (changed) ApplyTheme(light);

        _main?.OnThemeModeChanged();
    }

    /// <summary>托盘菜单切换语言后调用，把结果写进运行日志。</summary>
    public void AppendThemeLog(AppTheme mode) => _main?.AppendThemeLog(mode);

    /// <summary>
    /// 切换界面语言。
    ///
    /// 三处都要跟着变：资源字典（XAML 里 DynamicResource 绑的文字）、
    /// 从代码里取的文案（设备状态、徽标、托盘提示）、以及已经列出来的设备行。
    /// </summary>
    public void SetLanguage(AppLanguage language)
    {
        if (Settings.Language == language && Loc.Language == language) return;

        Settings.Language = language;
        Settings.Save();

        ApplyLanguage(language, save: false);

        // 托盘提示是托盘图标自己的文字，它不认资源字典，得手动重写一遍。
        UpdateTrayState(ConnectedCount);

        _main?.AppendLanguageLog(language);
    }

    /// <summary>
    /// 把某个语言的文案铺进资源字典。
    ///
    /// 换字典而不是改字典内容：WPF 会在字典被替换时重新解析所有 DynamicResource 绑定，
    /// 直接往字典里塞新值不一定能唤醒已经存在的绑定。
    /// </summary>
    private void ApplyLanguage(AppLanguage language, bool save)
    {
        Loc.SetLanguage(language);

        var dictionary = Loc.BuildResourceDictionary();
        var merged = Resources.MergedDictionaries;

        // 语言字典放在主题字典之后：资源查找是「后加入的优先」，
        // 这样语言资源不会盖掉主题里的同名键（真撞上了也能在 Loc 里改名）。
        // 换语言时整体替换同一位置，别把新字典追加到末尾——
        // 追加会让旧字典留在链上，键值会按「最后一个命中」取，还是旧的文案。
        if (_languageDictionary is not null)
        {
            var index = merged.IndexOf(_languageDictionary);
            if (index >= 0) merged[index] = dictionary;
            else merged.Add(dictionary);
        }
        else
        {
            merged.Add(dictionary);
        }

        _languageDictionary = dictionary;

        if (save) Settings.Save();
    }

    /// <summary>托盘菜单切换毛玻璃。材质只在主窗口上生效，交回窗口处理。</summary>
    public void SetMicaEnabled(bool enabled) => _main?.SetMicaEnabled(enabled);

    /// <summary>
    /// 托盘菜单切换开机自启动。
    /// 写注册表可能被组策略或安全软件拦掉，所以不看「想设成什么」，而是回头读注册表
    /// 的真实状态；返回值跟传入值不一致时，调用方负责把开关拨回去。
    /// </summary>
    public bool SetStartupEnabled(bool enabled)
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath)) StartupManager.SetEnabled(enabled, exePath);

        var actual = StartupManager.IsEnabled();
        if (Settings.StartWithWindows != actual)
        {
            Settings.StartWithWindows = actual;
            Settings.Save();
        }

        _main?.AppendStartupLog(actual);
        return actual;
    }

    public void DisconnectAllDevices() => _main?.DisconnectAllFromTray();

    private bool ResolveLightTheme() => Settings.Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => DetectLightTheme(),
    };

    /// <summary>系统主题变化。只有「自动」模式需要跟进，手动选择的不该被系统覆盖。</summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        if (Settings.Theme != AppTheme.Auto) return;

        Dispatcher.BeginInvoke(new Action(ApplyThemeMode));
    }

    /// <summary>
    /// 关机 / 注销前把 Mica 标记清掉。
    ///
    /// 不清的话下次启动会把这次「系统强制结束进程」误判成异常退出，
    /// 自动关掉用户的毛玻璃设置——而这种误判只发生在关机路径上，
    /// 用户完全无从察觉，只会觉得「设置老是自己变回去」。
    /// </summary>
    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        MicaGuard.Clear();
    }

    public void UpdateTrayState(int connectedCount)
    {
        ConnectedCount = connectedCount;

        if (_tray is null) return;

        var next = TrayIconFactory.Create(connectedCount > 0);
        var previous = _tray.Icon;
        _tray.Icon = next;
        previous?.Dispose();

        _tray.Text = connectedCount > 0
            ? Loc.Format("Tray.Tip.Count", connectedCount)
            : Loc.Get("Tray.Tip.None");
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        // 先把菜单关掉再干活：菜单是前台窗口，不关的话它会在下面这些同步操作
        // 期间一直挂在屏幕上。关它本身是幂等的，重复调用无害。
        var menu = _trayMenu;
        _trayMenu = null;
        menu?.Dismiss();

        try
        {
            AppLog.Write(Loc.Get("Log.ExitStart"));
            // 关掉音频连接可能要几百毫秒（蓝牙链路要拆），放在后面也没关系，
            // 但一定不能让它把 Shutdown 挡掉。
            _main?.PrepareShutdown();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            AppLog.Write(Loc.Get("Log.ExitDone"));
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.Format("Log.ExitFailed", ex.GetType().Name, ex.Message));
        }
        finally
        {
            MicaGuard.Clear();
            // ShutdownMode 是 OnExplicitShutdown，这句不执行进程就一直挂着：
            // 窗口都没了、消息泵还在，任务管理器里显示「未响应」。
            Shutdown();
        }
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTAudioSink");
            System.IO.Directory.CreateDirectory(folder);

            // 追加而不是覆盖：崩溃往往要复现好几次，覆盖写会把前一次的原因冲掉。
            var path = System.IO.Path.Combine(folder, "crash.log");
            var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{exception}{Environment.NewLine}";
            System.IO.File.AppendAllText(path, entry);
        }
        catch
        {
            // 连日志都写不了就只能放弃。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // --exit 助手没有启用过 Mica，不该替别人擦保护标记。
        if (!_exitHelper) MicaGuard.Clear();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        _tray?.Dispose();
        _mutex?.Dispose();
        // 退出信号句柄不释放的话，别的进程再开 --exit 会拿到一个已废弃的对象，
        // 通知就发不到这里了。
        _exitEvent?.Dispose();
        _exitEvent = null;
        base.OnExit(e);
    }

    // ---------- 主题 ----------

    private static bool DetectLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTheme(bool light)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(light ? "Theme/Light.xaml" : "Theme/Dark.xaml", UriKind.Relative),
        };
        Resources.MergedDictionaries[0] = dictionary;
    }

    // ---------- 托盘 ----------

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(false),
            Text = "BTAudioSink",
            Visible = true,
        };

        // 左键切换主弹窗，右键出菜单。不做 DoubleClick：它和两次 MouseUp 会打架。
        _tray.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ToggleMainFlyout();
            else if (args.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
    }

    /// <summary>
    /// 弹窗收起的时刻。点托盘图标时窗口会先失活收起，然后才轮到 MouseUp ——
    /// 不记这个时间戳，那一下会被当成「打开」，窗口关了又弹，永远关不掉。
    /// </summary>
    private DateTime _flyoutHiddenAt;

    /// <summary>左键点托盘：开着就收起，收着就弹出来。</summary>
    public void ToggleMainFlyout()
    {
        if (_main is null) return;

        if (_main.IsVisible)
        {
            _main.HideFlyout();
            return;
        }

        // 250ms 内刚被这次点击收起过，就当它是「关闭」，别再弹一次。
        if ((DateTime.UtcNow - _flyoutHiddenAt).TotalMilliseconds < 250) return;

        ShowMainWindow();
    }

    /// <summary>
    /// 每次右键都新建一个菜单窗口：它自带当前主题与设置状态，不用维护「菜单开着时刷新勾选」的逻辑。
    /// 旧菜单还开着就先关掉（快速连点右键的情况）。
    /// </summary>
    private void ShowTrayMenu()
    {
        if (_trayMenu is not null)
        {
            var stale = _trayMenu;
            _trayMenu = null;
            stale.Close();
        }

        var menu = new TrayMenuWindow(this);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null;
        };
        _trayMenu = menu;
        menu.ShowAtCursor();
    }
}

/// <summary>
/// 运行时画托盘图标：圆角方形底 + 蓝牙符号。
/// 未连接是中性灰，有设备连接变品牌蓝，任务栏上一眼分得出来。
/// </summary>
internal static class TrayIconFactory
{
    // 标准蓝牙折线，24x24 坐标系归一化后的原始点
    private static readonly (double X, double Y)[] BluetoothRune =
    {
        (6.5, 6.5), (17.5, 17.5), (12.0, 23.0),
        (12.0, 1.0), (17.5, 6.5), (6.5, 17.5),
    };

    /// <summary>
    /// 先按 4 倍尺寸画，再高质量缩回目标尺寸。
    /// 托盘图标只有 16~24px，蓝牙符号是 5 条 1.5px 的细线，GDI+ 在这个
    /// 尺寸直接描线经常断成虚线（尤其 45° 斜线）；放大画一遍相当于给每条
    /// 线做了超采样平均，缩回来线是连续的，圆角边缘也更干净。
    /// </summary>
    private const int Supersample = 4;

    /// <summary>
    /// 托盘槽位在 100% 缩放下是 16 物理像素。Windows 会把图标缩到槽位大小，
    /// 高 DPI 下槽位按比例变大（150% → 24px），如果只给 16px 的图，
    /// 系统只能把它放大，边缘就糊了。所以这里按当前显示器的 DPI 决定画多大。
    /// </summary>
    private const int TrayIconBaseSize = 16;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>托盘图标该用多少物理像素来画。</summary>
    private static int TrayIconSize()
    {
        var dpi = GetDpiForSystem();
        if (dpi == 0) return TrayIconBaseSize;

        var scaled = (int)Math.Round(TrayIconBaseSize * dpi / 96.0);
        return Math.Clamp(scaled, TrayIconBaseSize, 64);
    }

    public static Icon Create(bool connected)
    {
        // 按系统托盘实际尺寸画，150% 缩放下是 24px。
        var size = TrayIconSize();
        var canvas = size * Supersample;

        using var large = new Bitmap(canvas, canvas, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(large))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            Paint(graphics, canvas, size, connected);
        }

        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(large, new Rectangle(0, 0, size, size));
        }

        return ToIcon(bitmap);
    }

    /// <summary>按 <paramref name="canvas"/> 的像素尺寸绘制，比例全用画布边长的百分比。</summary>
    private static void Paint(Graphics graphics, int canvas, int size, bool connected)
    {
        using var path = RoundedRect(canvas);
        using var brush = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(canvas, canvas),
            connected ? Color.FromArgb(86, 162, 255) : Color.FromArgb(146, 146, 158),
            connected ? Color.FromArgb(22, 62, 190) : Color.FromArgb(96, 96, 108));
        graphics.FillPath(brush, path);

        var stroke = Math.Max(1.5f, size * 0.062f) * (canvas / (float)size);
        using var pen = new Pen(Color.White, stroke)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(pen, BluetoothPoints(canvas, size));
    }

    /// <summary>
    /// 把位图包成 PNG 压缩的 ICO（Vista 起系统原生支持这种条目）。
    /// 不能用 Bitmap.GetHicon()：它生成的图标会丢掉 alpha 通道，
    /// 圆角边缘那圈半透明像素会被当成不透明，在托盘上表现为一圈黑边，
    /// 深色任务栏下尤其明显。
    /// </summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();

        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write((ushort)0);                                  // 保留位
            writer.Write((ushort)1);                                  // 类型：图标
            writer.Write((ushort)1);                                  // 图像数量

            writer.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            writer.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            writer.Write((byte)0);                                    // 调色板数量
            writer.Write((byte)0);                                    // 保留位
            writer.Write((ushort)1);                                  // 色彩平面
            writer.Write((ushort)32);                                 // 位深
            writer.Write(0);                                          // 数据长度，写完回填
            writer.Write(22);                                         // 数据偏移 = 6 + 16
            writer.Flush();

            bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);

            var end = buffer.Position;
            writer.Seek(14, SeekOrigin.Begin);
            writer.Write((int)(end - 22));
            writer.Flush();
        }

        buffer.Position = 0;
        return new Icon(buffer, bitmap.Width, bitmap.Height);
    }

    private static GraphicsPath RoundedRect(int size)
    {
        var inset = size * 0.04f;
        var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
        var radius = size * 0.26f;
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static PointF[] BluetoothPoints(int canvas, int size)
    {
        // 符号占比跟 tools/make_icon.py 保持同一套：大图 56%；托盘这种 16~24px
        // 缩到 56% 白线会碎成虚线（本来就是 5 条细线挤在十几个像素里），
        // 所以小尺寸维持原来的 70%，只收 32px 及以上。
        var scale = size <= 24 ? 0.70 : size <= 32 ? 0.66 : 0.56;
        var center = canvas / 2.0;
        var points = new PointF[BluetoothRune.Length];
        for (var i = 0; i < BluetoothRune.Length; i++)
        {
            points[i] = new PointF(
                (float)(center + (BluetoothRune[i].X / 24.0 - 0.5) * canvas * scale),
                (float)(center + (BluetoothRune[i].Y / 24.0 - 0.5) * canvas * scale));
        }
        return points;
    }
}
