using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BTAudioSink.Services;

namespace Preview;

public static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    private struct POINT { public int X; public int Y; }

    private static string _log = "";

    [STAThread]
    public static void Main(string[] args)
    {
        // 不声明 DPI 感知的话，WPF 算出的屏幕点和 SetCursorPos 的实际落点会差一截。
        SetProcessDPIAware();

        // --readme：只生成 README 用的两张截图，不跑断言流程。
        var readmeMode = Array.Exists(args, a => a == "--readme");
        // 输出目录可用参数覆盖，方便从别处调用。
        var outArg = Array.FindIndex(args, a => a == "--out");
        var outDir = outArg >= 0 && outArg + 1 < args.Length ? args[outArg + 1] : AppContext.BaseDirectory;

        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var win = new PreviewWindow();
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -4000;
        win.Top = 0;
        win.ShowInTaskbar = false;
        win.Show();

        win.Dispatcher.InvokeAsync(async () =>
        {
            if (readmeMode)
            {
                await ReadmeShotsAsync(win, outDir);
            }
            else
            {
                await RunAsync(win);
            }
            win.Close();
            app.Shutdown();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        app.Run();
    }

    /// <summary>
    /// 生成 README 首页用的两张截图（浅色 / 深色）。
    ///
    /// 界面每改一次，README 上的图就过时了——GitHub 首页展示的是旧图，
    /// 比没有图更糟。所以这个流程要能随时重跑，而不是手工截图。
    /// </summary>
    private static async Task ReadmeShotsAsync(PreviewWindow win, string outDir)
    {
        Directory.CreateDirectory(outDir);

        foreach (var theme in new[] { "Light", "Dark" })
        {
            win.SetTheme(theme);
            win.SetReadmeBackdrop(theme);
            win.SetLanguage(AppLanguage.Chinese);
            win.SetDeviceCount(3);
            win.Select(-1);
            await Task.Delay(300);

            var name = theme == "Dark" ? "screenshot-dark.png" : "screenshot-light.png";
            var path = Path.Combine(outDir, name);
            win.CaptureCard(path);
            Console.WriteLine($"已生成 {path}");
        }
    }

    private static async Task RunAsync(PreviewWindow win)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(dir);
        _log = Path.Combine(dir, "log.txt");
        File.WriteAllText(_log, "");

        var outDir = AppContext.BaseDirectory;

        win.SetTheme("Light");
        win.SetDeviceCount(3);
        win.Select(-1);
        await Task.Delay(200);

        // 图 1：固定高度、3 台设备，看剩下的空白是否可接受
        win.Capture(Path.Combine(outDir, "state-fixed-3.png"));
        Say($"窗口高度={win.WindowHeight:0.#}（固定值，3 台设备）");
        Say($"  3 台：{win.ListScrollMetrics}");
        Say("对齐（3 台，无滚动条）：");
        Say(win.MeasureAlign());

        // 图 2：6 台设备——列表必须自己滚，窗口高度不变
        win.SetDeviceCount(6);
        await Task.Delay(250);
        win.Capture(Path.Combine(outDir, "state-fixed-6.png"));
        Say($"  6 台：{win.ListScrollMetrics}  窗口高度={win.WindowHeight:0.#}");
        Say("对齐（6 台，滚动条出现）：");
        Say(win.MeasureAlign());

        // 图 3：展开一行详情后仍固定
        win.SetDeviceCount(3);
        await Task.Delay(150);
        win.Select(0);
        await Task.Delay(450);
        win.Capture(Path.Combine(outDir, "state-fixed-row-open.png"));
        Say($"  展开详情：{win.ListScrollMetrics}  窗口高度={win.WindowHeight:0.#}");

        // 图 4：深色
        win.SetTheme("Dark");
        await Task.Delay(200);
        win.Capture(Path.Combine(outDir, "state-fixed-dark-log.png"));

        // 图 5：深色普通态（README 用的封面图）
        win.Select(-1);
        await Task.Delay(400);
        win.Capture(Path.Combine(outDir, "state-fixed-dark.png"));

        // 图 6 / 7：中英文对照。语言切换靠的是「换掉资源字典」，
        // 这两张图就是验证 WPF 会不会真的重新解析 DynamicResource 绑定。
        win.SetTheme("Light");
        win.SetDeviceCount(3);
        win.Select(0);
        await Task.Delay(450);

        win.SetLanguage(AppLanguage.Chinese);
        await Task.Delay(150);
        win.Select(0);
        await Task.Delay(450);
        win.Capture(Path.Combine(outDir, "lang-zh.png"));
        Say($"中文版：行状态={win.RowStatus(0)}  按钮={win.RowAction(0)}  徽标={win.Badge}  日志={win.LastLog}");

        // 底栏日志留两行，最长的常见句子装不装得下
        Say($"底栏日志宽度={win.LastLogWidth:0.#}px");
        Say($"  短日志时底栏高度={win.FooterHeight:0.#}");
        Say("  " + win.CheckLastLog(Loc.Format("Log.WakeAttempt", "大白", 1, 3)));
        Say("  " + win.CheckLastLog(Loc.Format("Log.WakeFailedMax", "大白", 3)));
        Say($"  两行日志时底栏高度={win.FooterHeight:0.#}（应与上面相同，否则列表会抖）");
        Say("  " + win.CheckLastLog(Loc.Format("Log.AutoStart", Loc.Get("Log.AutoReason.Startup"), 1)));
        win.Capture(Path.Combine(outDir, "log-two-lines-zh.png"));

        win.SetLanguage(AppLanguage.English);
        await Task.Delay(150);
        win.Select(0);
        await Task.Delay(450);
        win.Capture(Path.Combine(outDir, "lang-en.png"));
        Say($"英文版：行状态={win.RowStatus(0)}  按钮={win.RowAction(0)}  徽标={win.Badge}  日志={win.LastLog}");
        Say(win.MeasureRow());

        Say("  " + win.CheckLastLog(Loc.Format("Log.WakeAttempt", "iPad", 1, 3)));
        Say("  " + win.CheckLastLog(Loc.Format("Log.WakeFailedMax", "iPad", 3)));
        win.Capture(Path.Combine(outDir, "log-two-lines-en.png"));

        Say("done");
    }

    // ---------- 用真实鼠标点击验证「再点一次收起」 ----------

    private static async Task ClickTestAsync(PreviewWindow win)
    {
        GetCursorPos(out var saved);

        win.Topmost = true;
        win.Left = 120;
        win.Top = 120;
        win.Activate();
        win.Select(-1);
        win.UpdateLayout();
        await Task.Delay(600);

        Say("=== 点击测试（真实鼠标点击）===");
        Say($"  行为是否挂上={win.CollapseAttached} 窗口屏幕原点={win.ScreenOrigin}");

        // 1. 点行体 → 选中并展开
        Click(win.RowText(0));
        await Task.Delay(400);
        Say($"  点第 1 行行体 → SelectedIndex={win.SelectedIndex}（期望 0）");

        // 2. 再点同一行 → 收起
        Click(win.RowText(0));
        await Task.Delay(400);
        Say($"  再点同一行 → SelectedIndex={win.SelectedIndex}（期望 -1，即收起）");

        // 3. 重新展开后点「自动」方块 → 不该收起，且开关要切换
        Click(win.RowText(0));
        await Task.Delay(400);
        var autoBefore = win.IsAutoOn(0);
        Click(win.RowChip(0));
        await Task.Delay(400);
        Say($"  选中后点「自动」方块 → SelectedIndex={win.SelectedIndex}（期望 0，不该收起）" +
            $" 开关 {autoBefore} → {win.IsAutoOn(0)}（期望取反）");

        // 4. 选中第 1 行后点第 2 行 → 切行
        Click(win.RowText(1));
        await Task.Delay(450);
        Say($"  点第 2 行 → SelectedIndex={win.SelectedIndex}（期望 1）");

        SetCursorPos(saved.X, saved.Y);
    }

    /// <summary>把光标挪到元素中心，再按下和抬起左键。</summary>
    private static void Click(UIElement? target)
    {
        if (target is null)
        {
            Say("  目标元素为空，跳过这次点击");
            return;
        }

        var p = ScreenPoint(target);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
        Thread.Sleep(80);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>元素中心点的屏幕物理坐标。</summary>
    private static Point ScreenPoint(UIElement target)
    {
        var win = (PreviewWindow)Window.GetWindow(target)!;
        var local = new Point(target.RenderSize.Width / 2, target.RenderSize.Height / 2);
        var inWindow = target.TranslatePoint(local, win);
        var origin = win.ScreenOrigin;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
        return new Point(origin.X + inWindow.X * dpi.DpiScaleX, origin.Y + inWindow.Y * dpi.DpiScaleY);
    }

    // ---------- 工具 ----------

    private static void Say(string line) => File.AppendAllText(_log, line + Environment.NewLine);

    /// <summary>触发一次状态变化，然后按等间隔抓若干帧，同时记录内容高度。</summary>
    private static async Task Shoot(PreviewWindow win, string dir, string tag, int frames, int totalMs, Action trigger)
    {
        trigger();
        var step = totalMs / frames;

        for (var i = 0; i < frames; i++)
        {
            win.UpdateLayout();
            win.Capture(Path.Combine(dir, $"{tag}-{i}.png"), 1);
            Say($"  {tag}[{i}] t={i * step,3}ms  内容高度={win.ContentHeight:0.0}");
            await Task.Delay(step);
        }

        // 再等一会儿抓稳定态，确认动画结束后没有回弹。
        await Task.Delay(120);
        win.Capture(Path.Combine(dir, $"{tag}-final.png"), 1);
        Say($"  {tag}[final] 内容高度={win.ContentHeight:0.0}");
    }
}
