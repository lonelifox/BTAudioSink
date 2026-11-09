using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using BTAudioSink.Interop;
using BTAudioSink.Models;
using BTAudioSink.Services;
using Forms = System.Windows.Forms;

namespace BTAudioSink;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly AudioSinkService _audio = new();
    private readonly ObservableCollection<DeviceItem> _devices = new();

    /// <summary>用户在本次运行里主动断开的设备。手动断开就不该被自动重连拽回去，重启后按开关来。</summary>
    private readonly HashSet<string> _manualDisconnects = new();

    private bool _shuttingDown;
    /// <summary>第一次枚举结束前不显示「没找到设备」，否则一启动就闪一下空状态。</summary>
    private bool _enumerationDone;
    /// <summary>自动重连只在启动后跑一次，手动刷新不该把用户刚断开的设备又接回来。</summary>
    private bool _autoReconnectDone;
    /// <summary>弹窗刚显示的时刻。抢前台失败时系统会立刻发一次 Deactivated，不能当成「点外面」。</summary>
    private DateTime _shownAt;

    /// <summary>弹窗收起时通知 App，用来区分「点托盘收起」和「再点托盘打开」。</summary>
    public event EventHandler? FlyoutHidden;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        DeviceList.ItemsSource = _devices;
        DeviceList.SelectionChanged += OnDeviceSelectionChanged;

        _audio.DeviceFound += OnDeviceFound;
        _audio.DeviceLost += OnDeviceLost;
        _audio.ConnectionChanged += OnConnectionChanged;
        _audio.EnumerationCompleted += OnEnumerationCompleted;
        _audio.Log += AppendLog;

        // 语言一换，模型层那些「现取资源」的文字得重新通知界面。
        Loc.LanguageChanged += OnLanguageChanged;

        _audio.Start();

        // Mica 必须等窗口真正显示后再调，SourceInitialized 阶段 DWM 会拒绝。
        Loaded += OnWindowLoaded;

        // 点窗口外面就收起——托盘弹窗的基本行为。
        Deactivated += OnWindowDeactivated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };

        AppendLog(Loc.Get("Log.Started"));

        if (_app.MicaAutoDisabled)
        {
            AppendLog(Loc.Get("Log.MicaAutoDisabled"));
        }
    }

    // ---------- 设置 ----------

    /// <summary>
    /// 每台设备右侧的自动重连开关直接双向绑到 DeviceItem，这里只负责落盘和记日志。
    /// 绑定比 Checked/Unchecked 事件干净：模板加载时不会误触发一轮写配置。
    /// </summary>
    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DeviceItem device) return;
        if (e.PropertyName != nameof(DeviceItem.IsAutoReconnect)) return;

        var ids = _app.Settings.AutoReconnectIds;
        if (device.IsAutoReconnect)
        {
            if (!ids.Contains(device.Id)) ids.Add(device.Id);
            _manualDisconnects.Remove(device.Id);
            AppendLog(Loc.Format("Log.AutoReconnectOn", device.Name));
        }
        else
        {
            ids.Remove(device.Id);
            AppendLog(Loc.Format("Log.AutoReconnectOff", device.Name));
        }

        _app.Settings.Save();
    }

    // ---------- 外观 ----------

    /// <summary>外观变化后同步窗口材质。Mica 的底色是 DWM 按明暗给的，必须重设一次。</summary>
    public void OnThemeModeChanged()
    {
        ApplyWindowChromeColor();
        ApplyBackdrop();
    }

    /// <summary>托盘菜单切换毛玻璃。窗口层负责落盘、套用材质并记日志。</summary>
    public void SetMicaEnabled(bool enabled)
    {
        _app.Settings.UseMica = enabled;
        _app.Settings.Save();
        ApplyBackdrop();
        AppendLog(Loc.Get(enabled ? "Log.MicaOn" : "Log.MicaOff"));
    }

    public void AppendThemeLog(AppTheme mode)
    {
        var text = mode switch
        {
            AppTheme.Light => Loc.Get("Log.ThemeLight"),
            AppTheme.Dark => Loc.Get("Log.ThemeDark"),
            _ => Loc.Format("Log.ThemeAuto",
                Loc.Get(_app.IsLightTheme ? "Log.ThemeCurrent.Light" : "Log.ThemeCurrent.Dark")),
        };
        AppendLog(text);
    }

    /// <summary>托盘菜单切换开机自启动后调用，把结果写进运行日志。</summary>
    public void AppendStartupLog(bool enabled) =>
        AppendLog(Loc.Get(enabled ? "Log.StartupOn" : "Log.StartupOff"));

    /// <summary>托盘菜单切换语言后调用，把结果写进运行日志。</summary>
    public void AppendLanguageLog(AppLanguage language) =>
        AppendLog(Loc.Format("Log.LanguageChanged",
            Loc.Get(language == AppLanguage.English ? "Language.Name.En" : "Language.Name.Zh")));

    // ---------- 语言 ----------

    /// <summary>
    /// 语言切换的回调。资源字典那边 WPF 会自己重新解析 DynamicResource，
    /// 但设备行里那些「现取资源」的属性（状态、按钮文字、设备类型）得手动通知一次。
    /// </summary>
    private void OnLanguageChanged() => RefreshLocalizedText();

    /// <summary>刷新所有从代码里取的显示文字。</summary>
    public void RefreshLocalizedText()
    {
        foreach (var device in _devices) device.RefreshLocalizedText();
        UpdateBadge();
    }

    // ---------- 设备发现 ----------

    private void OnDeviceFound(string id, string name)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_devices.Any(device => device.Id == id)) return;

            var item = new DeviceItem(id, name)
            {
                IsAutoReconnect = _app.Settings.AutoReconnectIds.Contains(id),
            };
            // 先设初值再订阅，免得开关初值被当成一次用户操作写日志。
            item.PropertyChanged += OnDevicePropertyChanged;

            _devices.Add(item);
            AppendLog(Loc.Format("Log.DeviceFound", name));
            RefreshListState();
        }));
    }

    private void OnDeviceLost(string id)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var device = _devices.FirstOrDefault(item => item.Id == id);
            if (device is null) return;

            device.PropertyChanged -= OnDevicePropertyChanged;
            _devices.Remove(device);
            AppendLog(Loc.Format("Log.DeviceRemoved", device.Name));
            RefreshListState();
            UpdateBadge();
        }));
    }

    private void OnEnumerationCompleted()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _enumerationDone = true;
                RefreshListState();

                if (_devices.Count == 0)
                {
                    AppendLog(Loc.Get("Log.NoDevice"));
                    return;
                }

                if (_autoReconnectDone) return;
                _autoReconnectDone = true;
                StartAutoReconnect(Loc.Get("Log.AutoReason.Startup"));
            }
            catch (Exception ex)
            {
                AppendLog(Loc.Format("Log.AutoConnectError", ex.Message));
            }
        }));
    }

    /// <summary>自动连接最多试几次。链路正常时第一次就该成，连着失败基本是设备端不接，再试只是刷屏。</summary>
    private const int MaxAutoAttempts = 3;

    /// <summary>刚断开时盯链路的节奏。手机走进来通常几秒就重连，盯紧一点能马上接上。</summary>
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>盯了一分钟还没连上就放慢。这时候人可能走了，2 秒问一次纯属浪费。</summary>
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>勤快盯多久后转慢。别设成硬上限——手机晚点连上一样要能接回来。</summary>
    private static readonly TimeSpan FastPollWindow = TimeSpan.FromMinutes(1);

    /// <summary>正在给这些设备跑自动连接流程，避免同一条流程被启动两次。</summary>
    private readonly HashSet<string> _autoRunning = new();

    /// <summary>
    /// 自动连接的入口。每台设备各跑一条流程，不串行——一台设备等链路会把其它设备一起卡住。
    /// </summary>
    private void StartAutoReconnect(string reason)
    {
        var targets = _devices
            .Where(device => device.IsAutoReconnect && !device.IsConnected && !_audio.IsOpened(device.Id))
            .ToList();

        if (targets.Count == 0)
        {
            if (_devices.Any(device => device.IsAutoReconnect))
            {
                AppendLog(Loc.Get("Log.AutoSkip"));
            }
            return;
        }

        AppendLog(Loc.Format("Log.AutoStart", reason, targets.Count));
        foreach (var device in targets)
        {
            _ = AutoConnectAsync(device);
        }
    }

    /// <summary>
    /// 一台设备的自动连接流程。先看蓝牙链路落在哪一种状态，再决定动不动手：
    ///
    /// - <b>蓝牙链路未建立</b>：允许有限次唤醒尝试。实测 Windows 会主动 paging 设备，通常数秒内建立链路，
    ///   因此不能直接跳过；唤醒失败后应等待链路变化，避免反复调用 OpenAsync 造成持续超时。
    /// - <b>已经连通</b>：正常去切 A2DP。
    ///
    /// 两条线合计最多 <see cref="MaxAutoAttempts"/> 次；用完后停手，等链路重新建立
    /// （掉线后再连上 = 用户重新连了设备）才给新一轮，杜绝无限无效重试。
    /// 链路是「一直等」不是「限时等」：手机可能晚几分钟才连上来，限时等于直接放弃这种情况。
    ///
    /// 为什么不按信号强度提前拦死：曾经以为「信号 -128 = 蓝牙关了 = 连不上」，
    /// 结果 iPad 就在旁边、蓝牙开着、只是没连时同样是 -128。经典蓝牙的 RSSI 只在搜索/
    /// 建链时刷新，没链路就没有新样本，-128 只说明「没测到」，不能说「不在」。
    /// 判断能不能连的唯一可靠办法就是真去连一次，所以这里只控制次数。
    /// </summary>
    private async Task AutoConnectAsync(DeviceItem device)
    {
        var id = device.Id;
        var name = device.Name;

        if (!_autoRunning.Add(id)) return;

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // null 表示「还没判过」，用来只在状态翻转时写日志，避免几秒一条刷屏。
            BluetoothLinkState? seen = null;

            // 本次尝试次数（链路未建立时的唤醒尝试也计入）。
            var attempts = 0;

            // 次数用完了，安静等着，别再动手。
            var capped = false;

            while (CanAutoConnect(device))
            {
                // 连上了就走人：之后的意外断开会由 OnConnectionChanged 重新拉起一条流程。
                if (_audio.IsOpened(id)) return;

                var link = await _audio.QueryLinkAsync(id);

                if (link == BluetoothLinkState.NotLinked)
                {
                    if (seen != BluetoothLinkState.NotLinked)
                    {
                        seen = BluetoothLinkState.NotLinked;
                        device.SetStatus(DeviceStatus.BluetoothNotConnected);
                        AppendLog(Loc.Format("Log.LinkNotEstablished", name));
                    }

                    if (capped)
                    {
                        await Task.Delay(PollInterval(watch));
                        continue;
                    }

                    attempts++;
                    AppendLog(Loc.Format("Log.WakeAttempt", name, attempts, MaxAutoAttempts));

                    await ConnectAutoAsync(device);

                    if (await WaitOpenedAsync(id))
                    {
                        AppendLog(Loc.Format("Log.WakeSuccess", name));
                        return;
                    }

                    device.SetStatus(DeviceStatus.BluetoothNotConnected);

                    if (attempts >= MaxAutoAttempts)
                    {
                        capped = true;
                        AppendLog(Loc.Format("Log.WakeFailedMax", name, MaxAutoAttempts));
                    }
                    else
                    {
                        var wait = TimeSpan.FromSeconds(WakeBackoffSeconds(attempts));
                        AppendLog(Loc.Format("Log.WakeRetry", name, wait.TotalSeconds));
                        await Task.Delay(wait);
                    }

                    continue;
                }

                // ---- 链路通了（或者查不出来，当作通）----
                if (seen != link)
                {
                    seen = link;

                    // 从「没连」变「连上了」= 用户重新连了设备，给一轮新的次数。
                    attempts = 0;
                    capped = false;

                    AppendLog(link == BluetoothLinkState.Linked
                        ? Loc.Format("Log.LinkEstablished", name)
                        : Loc.Format("Log.LinkUnknown", name));
                }

                if (capped)
                {
                    await Task.Delay(PollInterval(watch));
                    continue;
                }

                attempts++;
                AppendLog(Loc.Format("Log.AutoAttempt", name, attempts, MaxAutoAttempts));

                await ConnectAutoAsync(device);

                if (await WaitOpenedAsync(id))
                {
                    AppendLog(Loc.Format("Log.AutoSuccess", name));
                    return;
                }

                if (attempts >= MaxAutoAttempts)
                {
                    capped = true;
                    device.SetStatus(DeviceStatus.ConnectFailed);
                    AppendLog(Loc.Format("Log.AutoFailedMax", name, MaxAutoAttempts));
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempts));
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog(Loc.Format("Log.AutoConnectErrorDetail", name, ex.Message));
        }
        finally
        {
            _autoRunning.Remove(id);
        }
    }

    /// <summary>
    /// 自动连接前的一步准备：等用户可能正在点的那一下做完，再真正发起连接。
    /// 真正的 OpenAsync 已经在服务层排好队（同一时刻只允许一台在连），
    /// 所以这里不用自己再排一次。
    /// </summary>
    private async Task ConnectAutoAsync(DeviceItem device)
    {
        if (!CanAutoConnect(device) || device.IsBusy || _audio.IsOpened(device.Id)) return;

        // 用户可能正好也在点连接，等它做完（最多 5 秒）。
        for (var wait = 0; wait < 10 && device.IsBusy; wait++)
        {
            await Task.Delay(500);
        }
        if (device.IsBusy) return;

        await ConnectAsync(device);
    }

    /// <summary>
    /// 等状态落定再判断。OpenAsync 返回成功那一瞬间 State 有时还是 Closed（系统异步翻状态），
    /// 立刻判失败会白白多试一轮，所以给它一眼的机会。
    /// </summary>
    private async Task<bool> WaitOpenedAsync(string id)
    {
        if (_audio.IsOpened(id)) return true;
        await Task.Delay(400);
        return _audio.IsOpened(id);
    }

    /// <summary>唤醒失败后的重试间隔：第一次 15 秒，第二次 45 秒，避免在链路未建立时频繁请求。</summary>
    private static int WakeBackoffSeconds(int attempts) => attempts switch
    {
        <= 1 => 15,
        _ => 45,
    };

    /// <summary>刚断开时盯紧一点；等过一分钟还没动静就放慢，别一直空转。</summary>
    private static TimeSpan PollInterval(System.Diagnostics.Stopwatch watch) =>
        watch.Elapsed < FastPollWindow ? FastPollInterval : SlowPollInterval;

    /// <summary>
    /// 这条自动连接流程还能不能继续：用户关开关、手动断开、设备被移除、程序退出，任何一条都要立刻收手。
    /// </summary>
    private bool CanAutoConnect(DeviceItem device)
    {
        if (_shuttingDown) return false;
        if (!_devices.Contains(device)) return false;
        if (!device.IsAutoReconnect) return false;
        if (_manualDisconnects.Contains(device.Id)) return false;
        return true;
    }

    /// <summary>
    /// 意外断开（手机走远、信号掉了、耳机进盒）后等 2 秒再走自动连接流程。
    /// 先等一下是因为断开瞬间状态还在变，立刻重连容易自己跟自己抢。
    /// </summary>
    private void ScheduleAutoReconnect(DeviceItem device)
    {
        if (!device.IsAutoReconnect || _shuttingDown) return;
        if (_manualDisconnects.Contains(device.Id)) return;

        // 已经有一条流程在盯这台设备了（它本来就在等链路/等重试），别插第二条。
        if (_autoRunning.Contains(device.Id)) return;

        AppendLog(Loc.Format("Log.DisconnectedSchedule", device.Name, MaxAutoAttempts));
        _ = ReconnectAfterDelayAsync(device);
    }

    private async Task ReconnectAfterDelayAsync(DeviceItem device)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));

        if (_shuttingDown || !_devices.Contains(device)) return;
        if (_audio.IsOpened(device.Id)) return;
        if (!CanAutoConnect(device)) return;

        await AutoConnectAsync(device);
    }

    /// <summary>
    /// 手动连接前先查询蓝牙链路。链路未建立时仍允许尝试（部分设备可由 OpenAsync 触发建链），
    /// 但先把原因写进日志，免得用户只看到一句「连接失败」。
    /// </summary>
    private async Task AppendLinkHintAsync(DeviceItem device)
    {
        if (await _audio.QueryLinkAsync(device.Id) == BluetoothLinkState.NotLinked)
        {
            AppendLog(Loc.Format("Log.LinkHint", device.Name));
        }
    }

    // ---------- 连接 ----------

    private async Task ConnectAsync(DeviceItem device)
    {
        if (device.IsBusy || device.IsConnected) return;

        device.IsBusy = true;
        device.SetStatus(DeviceStatus.Connecting);
        try
        {
            var ok = await _audio.ConnectAsync(device.Id);

            // 以服务层的真实状态为准，而不是「有没有收到事件」。
            // 对象早已 Opened 时服务层会直接纠正状态、不再发事件，只等事件会永远停在「正在连接…」。
            var opened = _audio.IsOpened(device.Id);
            device.IsConnected = opened;
            device.SetStatus(opened ? DeviceStatus.Connected
                : ok ? DeviceStatus.Disconnected
                : DeviceStatus.ConnectFailed);

            if (opened)
            {
                // 已经连上了，手动断开的记录作废，下次断开该自动重连还得重连。
                _manualDisconnects.Remove(device.Id);
            }
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            device.SetStatus(DeviceStatus.ConnectFailed);
            AppendLog(Loc.Format("Log.ErrorDetail", device.Name, ex.Message));
        }
        finally
        {
            device.IsBusy = false;
            UpdateBadge();
        }
    }

    private void DisconnectDevice(DeviceItem device)
    {
        // 只断开这一台；服务层会回传真实结果，失败时不改界面状态。
        if (_audio.Disconnect(device.Id, out var detail))
        {
            // 用户明确要断开，本次运行内别自动连回来。
            _manualDisconnects.Add(device.Id);
            AppendLog(Loc.Format("Log.Disconnected", device.Name));
        }
        else
        {
            AppendLog(Loc.Format("Log.DisconnectFailed", device.Name, detail));
        }

        // 兜底校准：不依赖回调，直接问服务层现在的真实状态。
        var opened = _audio.IsOpened(device.Id);
        device.IsConnected = opened;
        device.SetStatus(opened ? DeviceStatus.Connected : DeviceStatus.Disconnected, detail);
        UpdateBadge();
    }

    private void OnConnectionChanged(string id, bool connected, DeviceStatus status, string? detail)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown) return;

            var device = _devices.FirstOrDefault(item => item.Id == id);
            if (device is null) return;

            AppLog.Write(Loc.Format("Log.UiUpdate", device.Name,
                Loc.Get(connected ? "Status.Connected" : "Status.Disconnected"),
                detail ?? Loc.Get($"Status.{status}")));

            device.IsConnected = connected;
            device.SetStatus(status, detail);

            if (connected)
            {
                _manualDisconnects.Remove(id);
            }
            else
            {
                ScheduleAutoReconnect(device);
            }

            UpdateBadge();

            // 展开着详情的那一行，链路状态跟着变，别让它停在旧值上。
            if (ReferenceEquals(DeviceList.SelectedItem, device))
            {
                _ = LoadDeviceDetailsAsync(device);
            }
        }));
    }

    // ---------- 设备详情 ----------

    private async void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) return;
        await LoadDeviceDetailsAsync(device);
    }

    /// <summary>
    /// 现查一台设备的详情填进模型。查不到就写「—」，绝不拿默认值冒充真实信息——
    /// 这类界面的价值全在「显示的就是真的」，编一个 0 dBm 比空着更糟。
    /// </summary>
    private async Task LoadDeviceDetailsAsync(DeviceItem device)
    {
        try
        {
            var details = await _audio.QueryDetailsAsync(device.Id);

            if (details is null)
            {
                device.ClearAddress();
                device.LinkConnected = null;
                device.ClearRssi();
                device.ClassMajor = null;
                return;
            }

            device.AddressText = details.Address ?? Loc.Get("Detail.Unknown");
            device.LinkConnected = details.IsConnected;

            // 只有落在合理区间（负数、且强于 -121）才当真值显示。
            // 实测没连上的手机会报 12 这种正数，-128 / 0 也是哨兵值，
            // 它们都只说明「系统没测到」，不能冒充「信号强」或「信号弱」。
            // 判定放在 DeviceItem.RssiText 里，这样切语言时这句也跟着变。
            device.SetRssi(details.SignalStrength);

            device.ClassMajor = details.ClassMajor;
        }
        catch (Exception ex)
        {
            AppendLog(Loc.Format("Log.DetailFailed", device.Name, ex.Message));
        }
    }

    // ---------- 按钮 ----------

    /// <summary>
    /// 重新查找设备。已连接的条目保留（连接对象还在，刷新不影响它），
    /// 未连接的清掉，等新的 DeviceWatcher 重新报上来 —— 这样掉线/已取消配对的设备才会消失。
    /// </summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var stale = _devices.Where(device => !_audio.IsOpened(device.Id)).ToList();
        foreach (var device in stale)
        {
            _devices.Remove(device);
        }

        RefreshListState();

        _audio.Refresh();
        AppendLog(stale.Count > 0
            ? Loc.Format("Log.Refresh", stale.Count)
            : Loc.Get("Log.RefreshPlain"));
    }

    /// <summary>
    /// 强制重连一台设备：先断开把蓝牙链路释放干净，再重新走一遍打开流程。
    /// 断开时系统可能把其它设备一起报成 Closed，服务层会自动把它们接回来。
    /// </summary>
    private async void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceItem device }) return;
        if (device.IsBusy) return;

        device.IsBusy = true;
        device.SetStatus(DeviceStatus.Reconnecting);
        try
        {
            await AppendLinkHintAsync(device);

            if (_audio.IsOpened(device.Id))
            {
                AppendLog(Loc.Format("Log.ReconnectDisconnecting", device.Name));
                _audio.Disconnect(device.Id, out _);
                // 让系统把链路收干净再重开，否则 OpenAsync 容易直接返回失败。
                await Task.Delay(400);
            }

            var ok = await _audio.ConnectAsync(device.Id);
            var opened = _audio.IsOpened(device.Id);

            device.IsConnected = opened;
            device.SetStatus(opened ? DeviceStatus.Connected
                : ok ? DeviceStatus.Disconnected
                : DeviceStatus.ReconnectFailed);

            if (opened)
            {
                _manualDisconnects.Remove(device.Id);
                AppendLog(Loc.Format("Log.Reconnected", device.Name));
            }
            else
            {
                AppendLog(Loc.Format("Log.ReconnectFailed", device.Name));
            }
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            device.SetStatus(DeviceStatus.ReconnectFailed);
            AppendLog(Loc.Format("Log.ErrorDetail", device.Name, ex.Message));
        }
        finally
        {
            device.IsBusy = false;
            UpdateBadge();
        }
    }

    private async void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceItem device }) return;

        // 界面状态可能是过期的（断一台时系统会牵连把另一台报成 Closed），点之前先按真实状态校准。
        var opened = _audio.IsOpened(device.Id);
        if (device.IsConnected != opened)
        {
            AppLog.Write(Loc.Format("Log.Calibrate", device.Name,
                Loc.Get(device.IsConnected ? "Status.Connected" : "Status.Disconnected"),
                Loc.Get(opened ? "Status.Connected" : "Status.Disconnected")));
            device.IsConnected = opened;
            device.SetStatus(opened ? DeviceStatus.Connected : DeviceStatus.Disconnected);
        }

        if (opened) DisconnectDevice(device);
        else
        {
            await AppendLinkHintAsync(device);
            await ConnectAsync(device);
        }
    }

    /// <summary>托盘菜单的「全部断开」调这个，需要在 UI 线程执行。</summary>
    public void DisconnectAllFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(DisconnectAllFromTray));
            return;
        }

        // 全部断开是「这次别出声了」，不关掉各设备的自动重连开关，
        // 但本次运行内不允许它们被自动接回来，否则刚点完又连上。
        foreach (var device in _devices) _manualDisconnects.Add(device.Id);

        var closed = _audio.DisconnectAll();
        AppendLog(closed > 0
            ? Loc.Format("Log.DisconnectAll", closed)
            : Loc.Get("Log.DisconnectAllNone"));
    }

    // ---------- 窗口 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 句柄就绪后才能调 DWM，构造函数里调用拿不到 HWND。
        ApplyWindowChromeColor();
        ApplyWindowIcon();
    }

    private const int FlyInMs = 220;
    private const int FlyOutMs = 150;

    private Forms.Screen? _trayScreen;
    private bool _anchoredToTray;
    /// <summary>正在播滑入或滑出，这段时间不让 AnchorToTray 改坐标。</summary>
    private bool _flyoutAnimating;
    /// <summary>正在滑出。滑入被打断时也要能正常收起来，所以和 _flyoutAnimating 分开。</summary>
    private bool _flyoutClosing;

    /// <summary>
    /// 贴着托盘弹出：按任务栏所在的那条边决定位置，并从那个方向滑出来。
    /// 窗口是 SizeToContent，Show 时的高度还不是最终值（列表项还没生成），
    /// 所以等 Loaded 优先级再量；之后设备增删导致高度变化也继续贴边。
    /// </summary>
    public void ShowAtTray()
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _app.Dispatcher.BeginInvoke(new Action(ShowAtTray));
            return;
        }

        // 用「哪块屏幕」定下来，别跟着鼠标乱跳。
        _trayScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);

        _flyoutAnimating = false;
        _flyoutClosing = false;
        _anchoredToTray = true;
        Show();
        UpdateLayout();

        // 记下窗口此刻的真实位置：进场动画被打断时，退场要接着这里走。
        _liveLeft = Left;
        _liveTop = Top;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            BeginFlyIn();
            ForceForeground();
            _shownAt = DateTime.UtcNow;
            Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>任务栏那条边 → 弹窗目标位置。贴托盘图标那一头，同时避开任务栏本身。</summary>
    private (double Left, double Top) TargetPosition(out TrayEdge edge)
    {
        edge = TrayEdge.Bottom;
        if (_trayScreen is null) return (Left, Top);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return (Left, Top);

        // WPF 的 Left/Top 是 DIP，WinForms 的屏幕矩形是物理像素。
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0.1) scale = 1.0;

        var bounds = _trayScreen.Bounds;
        var area = _trayScreen.WorkingArea;

        var screenDip = new ScreenRect(
            bounds.Left / scale, bounds.Top / scale,
            bounds.Right / scale, bounds.Bottom / scale);
        var areaDip = new ScreenRect(
            area.Left / scale, area.Top / scale,
            area.Right / scale, area.Bottom / scale);

        edge = FlyoutPlacement.EdgeOf(screenDip, areaDip);
        var point = FlyoutPlacement.Place(edge, areaDip, width, height);
        return (point.Left, point.Top);
    }

    /// <summary>把窗口钉在目标位置。动画过程中不插手，否则会把滑动打断。</summary>
    private void AnchorToTray()
    {
        if (_trayScreen is null || !IsVisible || _flyoutAnimating) return;

        var (left, top) = TargetPosition(out _);
        Left = left;
        Top = top;
        _liveLeft = left;
        _liveTop = top;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_anchoredToTray) AnchorToTray();
    }

    /// <summary>
    /// 从任务栏那个方向滑进来：起点往任务栏一侧偏出去，再滑到目标位置。
    /// 用 Win32 直接挪窗口，不走 Window.Left/Top 动画 —— 后者每帧都会让 WPF
    /// 重建整个窗口表面，透明窗口在重建期间会露一块纯黑的底，肉眼可见地闪一下。
    /// </summary>
    private void BeginFlyIn()
    {
        var (left, top) = TargetPosition(out var edge);
        var (offsetX, offsetY) = FlyoutPlacement.EnterOffset(edge);

        _flyoutClosing = false;
        _flyoutAnimating = true;
        SlideWindow(left + offsetX, top + offsetY, left, top, FlyInMs, easeOut: true, () =>
        {
            _flyoutAnimating = false;
        });
    }

    /// <summary>收起弹窗。先往任务栏方向滑出去，滑完才真正 Hide。</summary>
    public void HideFlyout()
    {
        // 正在退场就别再启动一次；但正在进场时要允许打断——用户点了外面，
        // 不能因为「动画还没播完」就把窗口一直留在屏幕上。
        if (!IsVisible || _flyoutClosing) return;

        var (left, top) = TargetPosition(out var edge);
        var (offsetX, offsetY) = FlyoutPlacement.EnterOffset(edge);

        // 起点用窗口现在真实待着的位置：进场被打断时它还在半路上。
        var fromLeft = _liveLeft;
        var fromTop = _liveTop;

        _anchoredToTray = false;
        _flyoutClosing = true;
        _flyoutAnimating = true;
        SlideWindow(fromLeft, fromTop, left + offsetX, top + offsetY, FlyOutMs, easeOut: false, FinishHide);
    }

    private System.Windows.Threading.DispatcherTimer? _flyTimer;

    /// <summary>窗口最近一次被 SetWindowPos 摆到的位置（DIP），退场从这里接着走。</summary>
    private double _liveLeft;
    private double _liveTop;

    /// <summary>
    /// 逐帧调 SetWindowPos 平移窗口。DWM 会直接搬运已经合成好的表面，
    /// 不像 Left/Top 赋值那样触发重绘，所以滑动过程中不会闪黑。
    /// </summary>
    private void SlideWindow(double fromLeft, double fromTop, double toLeft, double toTop,
                             int durationMs, bool easeOut, Action completed)
    {
        _flyTimer?.Stop();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            Left = toLeft;
            Top = toTop;
            _liveLeft = toLeft;
            _liveTop = toTop;
            completed();
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0.1) scale = 1.0;

        void Move(double left, double top)
        {
            _liveLeft = left;
            _liveTop = top;
            SetWindowPos(
                handle, IntPtr.Zero,
                (int)Math.Round(left * scale), (int)Math.Round(top * scale),
                0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            // 15ms ≈ 一帧多一点点，滑动够顺，又不会把消息队列堵住。
            Interval = TimeSpan.FromMilliseconds(15),
        };

        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, watch.Elapsed.TotalMilliseconds / durationMs);
            // 三次曲线：进场先快后慢（像被弹出来），退场先慢后快（像收回去）。
            var k = easeOut ? 1 - Math.Pow(1 - t, 3) : t * t * t;

            Move(fromLeft + (toLeft - fromLeft) * k, fromTop + (toTop - fromTop) * k);

            if (t < 1) return;

            timer.Stop();
            _flyTimer = null;

            // SetWindowPos 绕过了 WPF，Left/Top 还停在旧值，这里补一次让它们同步，
            // 不然后续 AnchorToTray 会按旧坐标重新摆。
            Left = toLeft;
            Top = toTop;
            Move(toLeft, toTop);
            completed();
        };

        _flyTimer = timer;
        Move(fromLeft, fromTop);
        timer.Start();
    }

    private void FinishHide()
    {
        _flyTimer?.Stop();
        _flyTimer = null;
        _flyoutAnimating = false;
        _flyoutClosing = false;
        _anchoredToTray = false;
        Hide();
        FlyoutHidden?.Invoke(this, EventArgs.Empty);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // 刚弹出来的 250ms 内系统可能补发一次 Deactivated（抢前台失败时尤其常见），
        // 那个不是「用户点了外面」，忽略掉，否则窗口会一闪就没。
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 250) return;
        HideFlyout();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 点托盘图标时本进程并没有「拿到鼠标输入」，单纯 Activate() 可能不生效；
    /// 窗口没激活就收不到 Deactivated，点外面也就不会自己收回。
    /// </summary>
    private void ForceForeground()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetForegroundWindow(handle);
        Activate();
    }

    // ---------- 任务栏图标 ----------

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private System.Drawing.Icon? _bigIcon;
    private System.Drawing.Icon? _smallIcon;

    /// <summary>
    /// 直接把 ICO 里对应尺寸的那一帧交给 Windows。
    /// XAML 里 Window.Icon 指向 PNG 时，WPF 要先转成 HICON，再按系统尺寸重新缩放一次，
    /// 任务栏上那枚图标就会糊一层；改用 ICO 精确取帧可以省掉这次重采样。
    /// </summary>
    private void ApplyWindowIcon()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            // 大图标给任务栏和 Alt+Tab（逻辑 32px），小图标给标题栏（逻辑 16px）。
            // 按当前 DPI 换算成物理像素再去 ICO 里取帧，150% 下正好命中 48px 那一帧。
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var nextBig = LoadIconFrame((int)Math.Round(32 * dpi));
            var nextSmall = LoadIconFrame((int)Math.Round(16 * dpi));

            if (nextBig is not null)
                SendMessage(handle, WM_SETICON, ICON_BIG, nextBig.Handle);
            if (nextSmall is not null)
                SendMessage(handle, WM_SETICON, ICON_SMALL, nextSmall.Handle);

            // 换帧之前先把旧图标释放掉，否则每次切 DPI 都漏一批 GDI 句柄。
            var oldBig = _bigIcon;
            var oldSmall = _smallIcon;
            _bigIcon = nextBig;
            _smallIcon = nextSmall;
            oldBig?.Dispose();
            oldSmall?.Dispose();
        }
        catch
        {
            // 取不到就退回 XAML 里那枚，不影响功能。
        }
    }

    private static System.Drawing.Icon? LoadIconFrame(int size)
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
        if (stream is null) return null;

        // 多尺寸 ICO 里挑最接近的一帧，不会缩放
        return new System.Drawing.Icon(stream, new System.Drawing.Size(size, size));
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBackdrop();
    }

    /// <summary>主题切换后重新告诉 DWM 明暗，否则窗口边缘还停在旧配色上。</summary>
    public void ApplyWindowChromeColor()
    {
        DwmHelper.ApplyRoundedCorners(this);
        DwmHelper.ApplyColorMode(this, !_app.IsLightTheme);
    }

    /// <summary>
    /// 决定窗口用 Mica 还是实色底。必须同时满足：设置里开着、系统是 Win11、DWM 调用成功。
    /// 任一条不成立就退回实色，绝不留下半透明空壳。
    /// </summary>
    private void ApplyBackdrop()
    {
        var wantsMica = _app.Settings.UseMica && DwmHelper.IsWindows11;

        if (wantsMica)
        {
            // 顺序不能反：先把客户区划成玻璃区，DWM 才有地方铺材质。
            SetGlassFrame(true);
            if (DwmHelper.EnableMica(this) == 0)
            {
                DwmHelper.MakeCompositionTransparent(this, true);
                BackdropLayer.Visibility = Visibility.Collapsed;
                MicaGuard.MarkActive();
                return;
            }

            SetGlassFrame(false);
        }

        DwmHelper.DisableMica(this);
        SetGlassFrame(false);
        DwmHelper.MakeCompositionTransparent(this, false);
        BackdropLayer.Visibility = Visibility.Visible;
        MicaGuard.Clear();
    }

    /// <summary>
    /// Mica 只铺在 DWM 管辖的玻璃区域上。默认玻璃厚度是 0，等于整个客户区归 WPF 画，
    /// DWM 不会往上铺任何材质，结果就是一片黑。设成 -1 才是「整窗都算玻璃」。
    /// </summary>
    private void SetGlassFrame(bool mica)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is null) return;
        chrome.GlassFrameThickness = mica ? new Thickness(-1) : new Thickness(0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 弹窗没有关闭按钮；真有代码调 Close() 也只是收起。
        // 真正的关闭只发生在 ExitApp → Shutdown 之前。
        if (!_shuttingDown)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }

        base.OnClosing(e);
    }

    public void PrepareShutdown()
    {
        _shuttingDown = true;
        _app.Settings.Save();
        _audio.Dispose();
    }

    // ---------- 状态与日志 ----------

    private void RefreshListState()
    {
        var empty = _devices.Count == 0;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = empty && _enumerationDone ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBadge()
    {
        var count = _devices.Count(device => device.IsConnected);
        BadgeText.Text = count > 0
            ? Loc.Format("Ui.Badge.Count", count)
            : Loc.Get("Ui.Badge.None");
        _app.UpdateTrayState(count);
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        // 弹窗日常只露一两句，完整历史写进文件，需要时点「日志」用编辑器打开。
        AppLog.Write(message);

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LastLogText.Text = message;
        LastLogText.ToolTip = $"[{stamp}] {message}";
    }

    /// <summary>
    /// 「日志」按钮：用系统默认的文本编辑器打开日志文件。
    /// 不在程序里再列一个日志界面——文本编辑器能搜索、能选中复制，比只读列表顺手。
    /// </summary>
    private void OnLogButtonClick(object sender, RoutedEventArgs e)
    {
        if (!AppLog.TryOpen())
        {
            MessageBox.Show(
                Loc.Format("Msg.LogOpenFailed", AppLog.LogFilePath),
                "BTAudioSink",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
