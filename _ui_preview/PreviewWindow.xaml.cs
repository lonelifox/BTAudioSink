using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BTAudioSink.Services;

namespace Preview;

public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();

        // 主程序在窗口创建前就把语言字典挂上了，预览也得一样：
        // 否则 DynamicResource 解析不到，徽标之类的控件宽度全是 0，量出来是假的。
        Resources.MergedDictionaries.Add(Loc.BuildResourceDictionary());

        DeviceList.PreviewMouseLeftButtonDown += (_, _) => SpyHits++;

        SetDeviceCount(3);
    }

    public void SetTheme(string name)
    {
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new System.Uri($"Theme/{name}.xaml", System.UriKind.Relative),
        };
        UpdateLayout();
    }

    // ---------- 语言 ----------

    /// <summary>
    /// 切语言。做法跟主程序一致：换掉资源字典，WPF 会重新解析所有 DynamicResource 绑定。
    /// </summary>
    public void SetLanguage(AppLanguage language)
    {
        Loc.SetLanguage(language);

        var dictionary = Loc.BuildResourceDictionary();
        var merged = Resources.MergedDictionaries;
        if (merged.Count > 2) merged[2] = dictionary;
        else merged.Add(dictionary);

        UpdateLayout();

        // 列表里的文字是从代码取的（状态、按钮），得重新造一遍设备才会跟着换。
        SetDeviceCount(_deviceCount);
        BadgeText.Text = Loc.Format("Ui.Badge.Count", 1);
        LastLogText.Text = Loc.Format("Log.WakeRetry", "大白", 45);
    }

    // ---------- 设备数量（验滚动） ----------

    /// <summary>造 n 台设备。前三台用真实设备名，后面补占位，用来验列表会不会滚。</summary>
    private int _deviceCount = 3;

    public void SetDeviceCount(int count)
    {
        _deviceCount = count;

        var all = new List<Mock>
        {
            new()
            {
                Name = "HBer_iPad", StatusText = Loc.Get("Status.Connected"), IsConnected = true, IsAutoReconnect = true,
                ActionText = Loc.Get("Ui.Device.Disconnect"), AddressText = "C4:C3:6B:21:EE:C3", LinkText = Loc.Get("Detail.Linked"),
                RssiText = Loc.Get("Detail.RssiUnknown"), DeviceTypeText = Loc.Get("Class.1"),
            },
            new()
            {
                Name = "大白", StatusText = Loc.Get("Status.Disconnected"), IsConnected = false, IsAutoReconnect = false,
                ActionText = Loc.Get("Ui.Device.Connect"), AddressText = "9C:9E:D5:4D:55:50", LinkText = Loc.Get("Detail.NotLinked"),
                RssiText = "-62 dBm", DeviceTypeText = Loc.Get("Class.2"),
            },
            new()
            {
                Name = "EDIFIER W830NB 双金标版", StatusText = Loc.Get("Status.BluetoothNotConnected"), IsConnected = false, IsAutoReconnect = true,
                ActionText = Loc.Get("Ui.Device.Connect"), AddressText = "CC:14:BC:0D:90:1D", LinkText = Loc.Get("Detail.NotLinked"),
                RssiText = "-44 dBm", DeviceTypeText = Loc.Get("Class.4"),
            },
        };

        for (var i = all.Count; i < count; i++)
        {
            all.Add(new Mock
            {
                Name = $"备用设备 {i + 1}", StatusText = Loc.Get("Status.Disconnected"), IsConnected = false, IsAutoReconnect = false,
                ActionText = Loc.Get("Ui.Device.Connect"), AddressText = "00:11:22:33:44:55", LinkText = Loc.Get("Detail.NotLinked"),
                RssiText = Loc.Get("Detail.RssiUnknown"), DeviceTypeText = Loc.Get("Class.4"),
            });
        }

        DeviceList.ItemsSource = all.GetRange(0, Math.Min(count, all.Count));
        UpdateLayout();
    }

    public int VisibleRows
    {
        get
        {
            var sv = FindChild<ScrollViewer>(DeviceList);
            return sv is null ? -1 : (int)Math.Round(sv.ViewportHeight / 66.0);
        }
    }

    public bool ListOverflows
    {
        get
        {
            var sv = FindChild<ScrollViewer>(DeviceList);
            return sv is not null && sv.ExtentHeight > sv.ViewportHeight + 0.5;
        }
    }

    public string ListScrollMetrics
    {
        get
        {
            var sv = FindChild<ScrollViewer>(DeviceList);
            return sv is null
                ? "找不到 ScrollViewer"
                : $"viewport={sv.ViewportHeight:0.#} extent={sv.ExtentHeight:0.#} 可滚={sv.ExtentHeight > sv.ViewportHeight + 0.5}";
        }
    }

    public double WindowHeight => ActualHeight;

    /// <summary>容器上的焦点虚线是否已经关掉（null = 关掉）。</summary>
    public bool RowFocusVisualDisabled(int index) =>
        Container(index)?.FocusVisualStyle is null;

    public bool LogChipFocusVisualDisabled => LogButton.FocusVisualStyle is null;

    public Point ScreenOrigin => PointToScreen(new Point(0, 0));

    /// <summary>合成事件是否到达了列表这一层（排查路由问题用）。</summary>
    public int SpyHits;

    public bool CollapseAttached =>
        BTAudioSink.Behaviors.RowClickToggle.GetCollapseSelectedOnClick(DeviceList);

    public int SelectedIndex => DeviceList.SelectedIndex;

    public bool IsAutoOn(int index) => DeviceList.Items[index] is Mock m && m.IsAutoReconnect;

    /// <summary>选中/取消选中某一行，用来触发详情块的展开或收起动画。</summary>
    public void Select(int index) => DeviceList.SelectedIndex = index;

    /// <summary>直接测判定函数，绕开鼠标输入（不可靠时兜底用）。</summary>
    public bool TryCollapse(UIElement source) =>
        BTAudioSink.Behaviors.RowClickToggle.TryCollapse(DeviceList, source);

    /// <summary>窗口句柄，用来直接投递鼠标消息。</summary>
    public IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>把某个元素中心换算成客户区里的物理像素坐标（投递鼠标消息要用）。</summary>
    public Point ClientPoint(UIElement target)
    {
        var local = new Point(target.RenderSize.Width / 2, target.RenderSize.Height / 2);
        var inWindow = target.TranslatePoint(local, this);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(inWindow.X * dpi.DpiScaleX, inWindow.Y * dpi.DpiScaleY);
    }

    /// <summary>行里显示设备名的那个 TextBlock，用来模拟「点行体」。</summary>
    public UIElement? RowText(int index) => FindChild<TextBlock>(Container(index));

    /// <summary>行里的「自动」方块，用来模拟「点按钮」。</summary>
    public UIElement? RowChip(int index) => FindChild<CheckBox>(Container(index));

    private ListBoxItem? Container(int index) =>
        DeviceList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;

    // ---------- 语言切换的证据 ----------

    /// <summary>
    /// 行里状态文字的「真实渲染值」。
    /// 直接读界面上的 TextBlock，而不是回读模型——模型里的字符串是我们自己塞的，
    /// 只有界面上的这个才说明 DynamicResource 真的被重新解析了。
    /// </summary>
    public string RowStatus(int index) => RowTextByTag(index, "Status");

    /// <summary>行末连接/断开按钮上的真实渲染文字。</summary>
    public string RowAction(int index) => RowTextByTag(index, "Action");

    /// <summary>头部徽标的真实渲染文字。</summary>
    public string Badge => BadgeText.Text;

    /// <summary>底栏那行日志的真实渲染文字。</summary>
    public string LastLog => LastLogText.Text;

    /// <summary>
    /// 把一条日志塞进底栏，量它到底装不装得下。
    /// 做法：另起一个同样字号/行宽的探针 TextBlock 量完整文本需要多高，
    /// 再和底栏实际给的 MaxHeight（两行 = 30px）比。
    /// </summary>
    public string CheckLastLog(string text)
    {
        LastLogText.Text = text;
        UpdateLayout();

        var probe = new TextBlock
        {
            FontSize = TextElement.GetFontSize(LastLogText),
            FontFamily = TextElement.GetFontFamily(LastLogText),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = text,
        };

        probe.Measure(new Size(LastLogText.ActualWidth, double.PositiveInfinity));

        var need = probe.DesiredSize.Height;
        var box = LastLogText.ActualHeight;

        return $"「{text}」" + Environment.NewLine +
               $"    需要 {need:0.#}px / 实际 {box:0.#}px（约 {Math.Round(box / 15.0)} 行）→ " +
               (need <= box + 0.5 ? "完整显示" : "会被截断");
    }

    /// <summary>底栏文字块的实际宽度，一行能塞多少字就靠它算。</summary>
    public double LastLogWidth => LastLogText.ActualWidth;

    /// <summary>底栏整体高度。日志一行还是两行，这个数都不能变——否则上面的列表会跟着抖。</summary>
    public double FooterHeight => FooterBar.ActualHeight;

    /// <summary>
    /// 量三条竖线：刷新按钮右边缘、设备卡片右边缘、底栏分割线右边缘。
    /// 这三者必须落在同一个 x 上——「右上角图标和下方对齐」就是指这个。
    /// 有滚动条时也要成立，所以这个方法在 3 台和 6 台两种状态下各量一次。
    /// </summary>
    public string MeasureAlign()
    {
        var sb = new System.Text.StringBuilder();

        double Left(FrameworkElement? e) =>
            e is null ? -1 : e.TranslatePoint(new Point(0, 0), this).X;

        double Right(FrameworkElement? e) =>
            e is null ? -1 : e.TranslatePoint(new Point(e.ActualWidth, 0), this).X;

        var title = FindAll(this).OfType<TextBlock>().FirstOrDefault(t => t.Text == "BTAudioSink");
        var card = Container(0);

        sb.AppendLine($"窗口宽={ActualWidth:0.#}");
        sb.AppendLine($"  刷新按钮   右边缘={Right(RefreshButton):0.#}");
        sb.AppendLine($"  设备卡片   右边缘={Right(card):0.#}");
        sb.AppendLine($"  底栏分割线 右边缘={Right(FooterBar):0.#}");
        sb.AppendLine($"  日志按钮   右边缘={Right(LogButton):0.#} 宽={LogButton.ActualWidth:0.#}  " +
                      $"文字='{LogButton.Content}'");
        sb.AppendLine($"  徽标 左={Left(BadgeText):0.#} 右={Right(BadgeText):0.#}  " +
                      $"标题右边缘={Right(title):0.#}（徽标应紧跟标题）");
        sb.AppendLine($"  滚动条可见={FindChild<ScrollBar>(DeviceList)?.Visibility}");

        return sb.ToString();
    }

    /// <summary>
    /// 量一行里各部分的真实宽度。
    /// 英文比中文宽不少，按钮一挤，名字和状态就没地方了——
    /// 光靠肉眼判断不可靠，直接量。
    /// </summary>
    public string MeasureRow()
    {
        var sb = new System.Text.StringBuilder();

        var item = Container(0);
        if (item is null) return "没有第一行";

        var all = FindAll(item).ToList();
        var dot = all.OfType<Ellipse>().FirstOrDefault();
        var buttons = all.OfType<Button>().ToList();
        var chip = all.OfType<CheckBox>().FirstOrDefault();
        var nameBlock = all.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "Name");
        var statusBlock = all.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "Status");

        var stackWidth = buttons.Sum(b => b.ActualWidth) + (chip?.ActualWidth ?? 0)
            + 6 * Math.Max(0, buttons.Count);

        var textColumn = (nameBlock?.ActualWidth ?? 0) + 18;

        sb.AppendLine($"行宽={item.ActualWidth:0.#}  圆点={dot?.ActualWidth:0.#}  按钮栈={stackWidth:0.#}");
        sb.AppendLine($"名字列可用={textColumn:0.#}");
        sb.AppendLine($"  自动块={chip?.ActualWidth:0.#}  重连={buttons.ElementAtOrDefault(0)?.ActualWidth:0.#}  " +
                      $"主按钮={buttons.ElementAtOrDefault(1)?.ActualWidth:0.#}");
        sb.AppendLine($"  名字自然宽={NaturalWidth(nameBlock, "EDIFIER W830NB 双金标版"):0.#}  " +
                      $"当前='{nameBlock?.Text}'");
        sb.AppendLine($"  状态自然宽(BT未连)={NaturalWidth(statusBlock, Loc.Get("Status.BluetoothNotConnected")):0.#}  " +
                      $"当前='{statusBlock?.Text}'");
        sb.AppendLine($"  状态自然宽(已连)={NaturalWidth(statusBlock, Loc.Get("Status.Connected")):0.#}  " +
                      $"状态自然宽(未连)={NaturalWidth(statusBlock, Loc.Get("Status.Disconnected")):0.#}");

        // 英文状态文案候选：挑一个能塞进名字列的
        foreach (var candidate in new[]
        {
            "Bluetooth not connected", "Bluetooth down", "No BT link", "Not linked",
            "Bluetooth link down", "Disconnect failed", "Device removed", "Reconnecting…",
        })
        {
            sb.AppendLine($"  候选 '{candidate}' = {NaturalWidth(statusBlock, candidate):0.#}");
        }

        return sb.ToString();
    }

    /// <summary>一段文字在同样字号下的自然宽度（不受 TextTrimming 影响）。</summary>
    private static double NaturalWidth(FrameworkElement? like, string text)
    {
        if (like is null) return 0;

        var probe = new TextBlock
        {
            FontSize = TextElement.GetFontSize(like),
            FontFamily = TextElement.GetFontFamily(like),
            FontWeight = TextElement.GetFontWeight(like),
            Text = text,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private string RowTextByTag(int index, string tag)
    {
        if (Container(index) is not { } item) return "(没有这一行)";

        foreach (var element in FindAll(item))
        {
            if (element.Tag as string != tag) continue;

            // 按钮的话看 Content，文本块的话看 Text。
            if (element is TextBlock block) return block.Text;
            if (element is ContentControl control) return control.Content?.ToString() ?? "";
        }

        return "(找不到 " + tag + ")";
    }

    /// <summary>收集视觉树里的所有元素。</summary>
    private static IEnumerable<FrameworkElement> FindAll(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element)
            {
                yield return element;
                foreach (var nested in FindAll(element)) yield return nested;
            }
            else
            {
                foreach (var nested in FindAll(child)) yield return nested;
            }
        }
    }

    /// <summary>行内某点的屏幕坐标（设备像素）。xRatio 用来避开右侧按钮区。</summary>
    public Point? RowPoint(int index, double xRatio)
    {
        if (DeviceList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return null;
        var local = new Point(item.ActualWidth * xRatio, item.ActualHeight / 2);
        var onWindow = item.TranslatePoint(local, this);
        var origin = PointToScreen(new Point(0, 0));
        return new Point(origin.X + onWindow.X, origin.Y + onWindow.Y);
    }

    /// <summary>行内第一个指定类型子元素的屏幕坐标，用来精确点中「自动」方块。</summary>
    public Point? ChildPoint<T>(int index) where T : FrameworkElement
    {
        if (DeviceList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return null;
        var target = FindChild<T>(item);
        if (target is null) return null;

        var local = new Point(target.ActualWidth / 2, target.ActualHeight / 2);
        var onWindow = target.TranslatePoint(local, this);
        var origin = PointToScreen(new Point(0, 0));
        return new Point(origin.X + onWindow.X, origin.Y + onWindow.Y);
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>当前内容高度，动画过程中会一帧帧变大，用来做数值证据。</summary>
    public double ContentHeight => ((FrameworkElement)Content).ActualHeight;

    public void Capture(string path, int scale = 2)
    {
        UpdateLayout();

        var root = (FrameworkElement)Content;
        var w = (int)System.Math.Ceiling(root.ActualWidth);
        var h = (int)System.Math.Ceiling(root.ActualHeight);

        var bmp = new RenderTargetBitmap(w * scale, h * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // 先铺一层背景，否则透明区域截图是全黑。
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("WindowBgBrush"), null, new Rect(0, 0, w, h));
        }
        bmp.Render(dv);
        bmp.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// 生成 README 用的截图：带圆角和投影，四周留白。
    ///
    /// 直接抓窗口内容出来是一块硬邦邦的矩形，贴在 GitHub 首页很生硬。
    /// 这里用 DrawingVisual 画一个带阴影的圆角卡片，看起来就是真实的窗口。
    ///
    /// 阴影靠两层半透明黑矩形错位叠出来——不引入额外的渲染依赖，
    /// 效果够用，而且完全确定性（同样的输入永远出同样的图）。
    /// </summary>
    public void CaptureCard(string path, int scale = 2, int padding = 24)
    {
        UpdateLayout();

        var root = (FrameworkElement)Content;
        var w = (int)System.Math.Ceiling(root.ActualWidth);
        var h = (int)System.Math.Ceiling(root.ActualHeight);

        // 阴影要往外扩，所以画布比内容大一圈
        var totalW = w + padding * 2;
        var totalH = h + padding * 2;

        var bmp = new RenderTargetBitmap(totalW * scale, totalH * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // 先把窗口内容渲染成一张位图，后面叠到卡片上。
        // 位图是 w*scale 像素、DPI 96*scale，所以它自身的逻辑尺寸是 w DIP。
        // DrawImage 按源 DPI 换算，直接给像素尺寸的矩形会被再放大一次——
        // 用 PushTransform 显式做缩放，矩形就用 DIP 单位，最不容易搞混。
        var content = new RenderTargetBitmap(w * scale, h * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var cdv = new DrawingVisual();
        using (var dc = cdv.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("WindowBgBrush"), null, new Rect(0, 0, w, h));
        }
        content.Render(cdv);
        content.Render(root);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 背景留白：浅色主题用极浅的灰，深色主题用极深的灰
            var backdrop = _readmeBackdrop;
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, totalW, totalH));

            var radius = 12.0;

            // 阴影：三层错位的半透明圆角矩形，越远越淡
            for (var i = 3; i >= 1; i--)
            {
                var off = i * 3.0;
                var alpha = (byte)(10 - i * 2);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
                dc.DrawRoundedRectangle(brush, null,
                    new Rect(padding - off + 1, padding - off + 2, w + off * 2, h + off * 2),
                    radius + off, radius + off);
            }

            // 卡片本体
            var bg = (Brush)FindResource("WindowBgBrush");
            dc.DrawRoundedRectangle(bg, null, new Rect(padding, padding, w, h), radius, radius);

            // 卡片边框：浅色主题下有一圈极淡的描边才不至于和背景糊在一起
            var border = new Pen(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), 1);
            dc.DrawRoundedRectangle(null, border, new Rect(padding, padding, w, h), radius, radius);
        }
        bmp.Render(dv);

        // 把窗口内容画到卡片上。DrawImage 直接吃位图，
        // 不用 Render（那个只收 Visual）。
        // 位图自身的逻辑尺寸已经是 w DIP（因为 DPI 标了 96*scale），
        // 所以这里直接按 DIP 画，交给 DrawImage 自己换算成像素。
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(content, new Rect(padding, padding, w, h));
        }
        bmp.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>README 截图的底色。浅色用近白，深色用近黑，都不抢内容。</summary>
    private Brush _readmeBackdrop = new SolidColorBrush(Color.FromRgb(246, 247, 249));

    public void SetReadmeBackdrop(string theme)
    {
        _readmeBackdrop = theme == "Dark"
            ? new SolidColorBrush(Color.FromRgb(24, 25, 27))
            : new SolidColorBrush(Color.FromRgb(246, 247, 249));
    }

    private sealed class Mock
    {
        public string Name { get; set; }
        public string StatusText { get; set; }
        public bool IsConnected { get; set; }
        public bool IsAutoReconnect { get; set; }
        public string ActionText { get; set; }
        public string AddressText { get; set; }
        public string LinkText { get; set; }
        public string RssiText { get; set; }
        public string DeviceTypeText { get; set; }
    }
}
