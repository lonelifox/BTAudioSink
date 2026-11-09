using System;
using System.Collections.Generic;
using System.Windows;

namespace BTAudioSink.Services;

/// <summary>界面语言。</summary>
public enum AppLanguage
{
    /// <summary>简体中文。</summary>
    Chinese,

    /// <summary>English.</summary>
    English,
}

/// <summary>
/// 全部界面文案的唯一出处。
///
/// 为什么不用 resx + 卫星程序集：那套要额外的构建步骤和工具链，而这里的文案量不大，
/// 两个字典放在一个文件里反而更好读、更好改。
///
/// 两种取用方式：
/// - XAML 里写 <c>{DynamicResource Ui.Log.Button}</c>，语言一换就跟着变；
/// - 代码里写 <c>Loc.Get("Ui.Log.Button")</c> 或 <c>Loc.Format("Log.Xxx", 参数)</c>。
///
/// 带参数的文案用 <c>{0}</c> 占位，交给 <see cref="Format"/> 填。
/// </summary>
public static class Loc
{
    // ---------- 中文 ----------

    private static readonly Dictionary<string, string> Zh = new()
    {
        // 界面
        ["Ui.Empty"] = "没有找到已配对的蓝牙设备\n请先打开蓝牙并在系统设置里完成配对",
        ["Ui.Device.Auto"] = "自动",
        ["Ui.Device.AutoTip"] = "自动重连：这台设备意外断开或下次启动时自动连回来；手动断开则不连",
        ["Ui.Device.Reconnect"] = "重连",
        ["Ui.Device.ReconnectTip"] = "重新连接：先断开再连一次",
        ["Ui.Device.ActionTip"] = "连接或断开这台设备的音频接收",
        ["Ui.Device.Connect"] = "连接",
        ["Ui.Device.Disconnect"] = "断开",
        ["Ui.Detail.Address"] = "蓝牙地址",
        ["Ui.Detail.Link"] = "蓝牙链路",
        ["Ui.Detail.Rssi"] = "信号强度",
        ["Ui.Detail.Type"] = "设备类型",
        ["Ui.Log.Button"] = "日志",
        ["Ui.Log.Tip"] = "用默认文本编辑器打开日志文件（%APPDATA%\\BTAudioSink\\log.txt）",
        ["Ui.Log.Ready"] = "就绪",
        ["Ui.RefreshTip"] = "重新查找蓝牙设备",
        ["Ui.Badge.None"] = "未连接",
        ["Ui.Badge.Count"] = "{0} 台已连接",

        // 设备状态
        ["Status.Disconnected"] = "未连接",
        ["Status.Connected"] = "已连接",
        ["Status.Connecting"] = "正在连接…",
        ["Status.ConnectFailed"] = "连接失败",
        ["Status.Reconnecting"] = "正在重新连接…",
        ["Status.ReconnectFailed"] = "重连失败",
        ["Status.BluetoothNotConnected"] = "蓝牙未连接",
        ["Status.Removed"] = "设备已移除",
        ["Status.DisconnectFailed"] = "断开失败",

        // 详情
        ["Detail.Linked"] = "已连通",
        ["Detail.NotLinked"] = "未连通",
        ["Detail.Unknown"] = "—",
        ["Detail.RssiUnknown"] = "未测到",

        // 设备类别（蓝牙 Class of Device 主类别码）
        ["Class.0"] = "杂项设备",
        ["Class.1"] = "电脑",
        ["Class.2"] = "手机",
        ["Class.3"] = "网络设备",
        ["Class.4"] = "音频设备",
        ["Class.5"] = "外设",
        ["Class.6"] = "成像设备",
        ["Class.7"] = "可穿戴设备",
        ["Class.8"] = "玩具",
        ["Class.9"] = "医疗设备",
        ["Class.Unknown"] = "未知",

        // 托盘菜单
        ["Tray.Open"] = "打开主界面",
        ["Tray.DisconnectAll"] = "全部断开",
        ["Tray.Appearance"] = "外观",
        ["Tray.Theme.Auto"] = "自动",
        ["Tray.Theme.Light"] = "浅色",
        ["Tray.Theme.Dark"] = "深色",
        ["Tray.Mica"] = "毛玻璃背景",
        ["Tray.MicaTip"] = "窗口后面透出壁纸的模糊层",
        ["Tray.MicaUnsupported"] = "需要 Windows 11",
        ["Tray.Startup"] = "开机自启动",
        ["Tray.Exit"] = "退出",
        ["Tray.Language"] = "语言",
        ["Tray.Language.Zh"] = "中文",
        ["Tray.Language.En"] = "English",
        ["Tray.MenuTitle"] = "托盘菜单",
        // 语言的本名：写进日志时用这个。
        ["Language.Name.Zh"] = "中文",
        ["Language.Name.En"] = "英文",
        ["Tray.Tip.None"] = "BTAudioSink · 未连接",
        ["Tray.Tip.Count"] = "BTAudioSink · {0} 台已连接",

        // 提示框
        ["Msg.AlreadyRunning"] = "程序已经在运行了，请从托盘图标打开。",
        ["Msg.LogOpenFailed"] = "无法打开日志文件：\n{0}",

        // 运行日志
        ["Log.Started"] = "程序已启动，正在枚举已配对的蓝牙设备。",
        ["Log.MicaAutoDisabled"] = "检测到上次启用 Mica 背景后程序异常退出，本次已自动禁用该背景，以避免窗口渲染异常。",
        ["Log.AutoReconnectOn"] = "{0}：已启用自动重连，连接断开后将自动重新建立。",
        ["Log.AutoReconnectOff"] = "{0}：已关闭自动重连。",
        ["Log.MicaOn"] = "已启用 Mica 背景。",
        ["Log.MicaOff"] = "已禁用 Mica 背景。",
        ["Log.ThemeLight"] = "外观已切换为浅色。",
        ["Log.ThemeDark"] = "外观已切换为深色。",
        ["Log.ThemeAuto"] = "外观已改为跟随系统（当前{0}）。",
        ["Log.ThemeCurrent.Light"] = "浅色",
        ["Log.ThemeCurrent.Dark"] = "深色",
        ["Log.StartupOn"] = "已加入开机自启动。",
        ["Log.StartupOff"] = "已取消开机自启动。",
        ["Log.DeviceFound"] = "发现设备：{0}",
        ["Log.DeviceRemoved"] = "设备已移除：{0}",
        ["Log.NoDevice"] = "未发现可用设备，请确认蓝牙已开启且设备已完成配对。",
        ["Log.AutoConnectError"] = "自动连接过程中发生错误：{0}",
        ["Log.AutoSkip"] = "已启用自动连接的设备均已连接或无需处理，本次跳过。",
        ["Log.AutoStart"] = "{0}：{1} 台设备已启用自动连接，开始检查蓝牙链路状态…",
        ["Log.AutoReason.Startup"] = "启动时",
        ["Log.LinkNotEstablished"] = "{0}：蓝牙链路未建立，设备尚未连接到本机。",
        ["Log.WakeAttempt"] = "{0}：蓝牙链路未建立，尝试通过音频连接唤醒设备（{1}/{2}）…",
        ["Log.WakeSuccess"] = "{0}：设备已唤醒，A2DP 音频连接已建立。",
        ["Log.WakeFailedMax"] = "{0}：唤醒失败，已达到最大尝试次数（{1} 次），暂停自动连接。请先在设备端建立蓝牙连接，或手动点击“连接”。",
        ["Log.WakeRetry"] = "{0}：本次唤醒未成功，将于 {1:0} 秒后重试。",
        ["Log.LinkEstablished"] = "{0}：蓝牙链路已建立，开始建立 A2DP 音频连接。",
        ["Log.LinkUnknown"] = "{0}：无法确定蓝牙链路状态，将直接尝试建立音频连接。",
        ["Log.AutoAttempt"] = "{0}：自动连接第 {1}/{2} 次…",
        ["Log.AutoSuccess"] = "{0}：自动连接成功。",
        ["Log.AutoFailedMax"] = "{0}：自动连接连续 {1} 次未成功，暂停自动重试。重新建立蓝牙链路或手动点击“连接”后才会再次尝试。",
        ["Log.AutoConnectErrorDetail"] = "{0}：自动连接出错：{1}",
        ["Log.DisconnectedSchedule"] = "{0}：连接已断开，将于 2 秒后开始自动重连（最多 {1} 次）…",
        ["Log.LinkHint"] = "{0}：蓝牙链路未建立，仍将尝试建立音频连接。",
        ["Log.Disconnected"] = "已断开：{0}",
        ["Log.DisconnectFailed"] = "断开 {0} 失败：{1}",
        ["Log.DetailFailed"] = "读取 {0} 详情失败：{1}",
        ["Log.Refresh"] = "已刷新设备列表，正在重新扫描（已移除 {0} 条未连接记录）。",
        ["Log.RefreshPlain"] = "已刷新设备列表，正在重新扫描。",
        ["Log.ReconnectDisconnecting"] = "重新连接 {0}：先断开现有连接",
        ["Log.Reconnected"] = "已重新连接：{0}",
        ["Log.ReconnectFailed"] = "重新连接失败：{0}",
        ["Log.DisconnectAll"] = "已断开全部设备（{0} 台）。",
        ["Log.DisconnectAllNone"] = "没有处于连接状态的设备。",
        ["Log.LanguageChanged"] = "界面语言已切换为{0}。",
        ["Log.ErrorDetail"] = "{0}：{1}",
        ["Log.SessionCreated"] = "连接对象已建立 {0}：obj=0x{1:X8} id={2}",
        ["Log.NoneShort"] = "无",

        // 服务层日志
        ["Log.ScanFailed"] = "启动蓝牙扫描失败：{0}",
        ["Log.ScanHint"] = "请确认蓝牙已打开，且适配器支持 A2DP Sink。",
        ["Log.RefreshWatcher"] = "刷新设备列表：重建 DeviceWatcher",
        ["Log.StopWatcherFailed"] = "停止设备扫描失败：{0}: {1}",
        ["Log.ConnectSkip"] = "连接 {0} 跳过：已存在连接对象（State={1}），按实际状态同步界面",
        ["Log.StillConnected"] = "{0}：检测到音频连接仍然有效，已同步界面状态。",
        ["Log.StaleObject"] = "{0}：连接对象仍存在，但状态为 {1}，将释放后重新连接…",
        ["Log.ConnectStart"] = "开始连接 {0}",
        ["Log.CreateFailed"] = "创建连接对象失败：{0}",
        ["Log.NotSupported"] = "该设备不支持音频接收连接（蓝牙适配器或驱动可能不支持 A2DP Sink）。",
        ["Log.ConnectOk"] = "连接成功 {0}（State={1}）",
        ["Log.ConnectFailed"] = "连接失败 {0}：{1}",
        ["Log.ConnectError"] = "连接时出错 {0}：{1}: {2}",
        ["Log.DisconnectNoSession"] = "断开 {0}：_live 中没有该连接，仅同步界面状态",
        ["Log.UnsubscribeFailed"] = "退订状态回调失败 {0}：{1}: {2}",
        ["Log.DisconnectTrace"] = "断开 {0}：关闭前={1} 异常={2} 剩余连接={3} 其它设备[{4}] → 关闭后[{5}]",
        ["Log.DisconnectFailedDetail"] = "断开失败 {0}：{1}",
        ["Log.CascadeCheck"] = "联动检查：{0} 状态仍为 Opened，无需恢复",
        ["Log.CascadeRestore"] = "联动恢复：{0} 的连接被系统一并关闭，正在重新打开",
        ["Log.CascadeRestored"] = "{0}：连接因系统联动而断开，已自动重新连接。",
        ["Log.ReleaseFailed"] = "释放连接失败：{0}: {1}",
        ["Log.StateCallbackReleased"] = "状态回调（连接已由本程序释放，忽略）{0}",
        ["Log.StateCallbackCascade"] = "状态回调 {0} → {1}（系统联动，暂不更新界面）　其它设备[{2}]",
        ["Log.StateCallback"] = "状态回调 {0} → {1}　其它设备[{2}]",
        ["Log.DeviceRemovedSys"] = "设备已移除（系统通知）：{0}",
        ["Log.ExitStart"] = "退出：开始清理（关闭音频连接、移除托盘图标）",
        ["Log.ExitDone"] = "退出：清理完成",
        ["Log.ExitFailed"] = "退出时清理失败：{0}: {1}",
        ["Log.UiUpdate"] = "界面更新：{0} → {1}（{2}）",
        ["Log.Calibrate"] = "校准界面状态：{0} {1} → {2}",
        ["Log.LinkQueryFailed"] = "蓝牙链路查询失败：{0}: {1}",
        ["Log.DetailQueryFailed"] = "读取设备详情失败：{0}: {1}",

        // 失败原因
        ["Fail.Timeout"] = "设备未在超时时间内响应。请在设备端确认连接，或在其蓝牙设备列表中选择本机。",
        ["Fail.Denied"] = "连接请求被系统拒绝。请确认蓝牙已开启，且设备仍处于配对状态。",
        ["Fail.Unknown"] = "系统返回未知失败（{0}）。",
        ["Fail.UnknownNoDetail"] = "系统返回未知失败，未提供详细信息。",
        ["Fail.Status"] = "连接失败：{0}",
        ["Fail.ConnectionMayStillBeActive"] = "{0}（连接可能仍在）",
        ["Fail.ReadFailed"] = "读取失败({0})",

        // 设备
        ["Device.FallbackName"] = "设备#{0}",
    };

    // ---------- 英文 ----------

    private static readonly Dictionary<string, string> En = new()
    {
        // UI
        ["Ui.Empty"] = "No paired Bluetooth devices found\nTurn on Bluetooth and pair the device in Windows Settings first",
        ["Ui.Device.Auto"] = "Auto",
        ["Ui.Device.AutoTip"] = "Auto-reconnect: reconnect automatically after an unexpected disconnect or on next launch. Manual disconnects are not reconnected.",
        ["Ui.Device.Reconnect"] = "Reconnect",
        ["Ui.Device.ReconnectTip"] = "Reconnect: drop the current connection first, then connect again",
        ["Ui.Device.ActionTip"] = "Connect or disconnect this device's audio sink",
        ["Ui.Device.Connect"] = "Connect",
        ["Ui.Device.Disconnect"] = "Disconnect",
        ["Ui.Detail.Address"] = "Address",
        ["Ui.Detail.Link"] = "Bluetooth link",
        ["Ui.Detail.Rssi"] = "Signal strength",
        ["Ui.Detail.Type"] = "Device type",
        ["Ui.Log.Button"] = "Logs",
        ["Ui.Log.Tip"] = "Open the log file in the default text editor (%APPDATA%\\BTAudioSink\\log.txt)",
        ["Ui.Log.Ready"] = "Ready",
        ["Ui.RefreshTip"] = "Rescan for Bluetooth devices",
        ["Ui.Badge.None"] = "Not connected",
        ["Ui.Badge.Count"] = "{0} connected",

        // Device status
        ["Status.Disconnected"] = "Not connected",
        ["Status.Connected"] = "Connected",
        ["Status.Connecting"] = "Connecting…",
        ["Status.ConnectFailed"] = "Connection failed",
        ["Status.Reconnecting"] = "Reconnecting…",
        ["Status.ReconnectFailed"] = "Reconnect failed",
        ["Status.BluetoothNotConnected"] = "No BT link",
        ["Status.Removed"] = "Device removed",
        ["Status.DisconnectFailed"] = "Disconnect failed",

        // Details
        ["Detail.Linked"] = "Linked",
        ["Detail.NotLinked"] = "Not linked",
        ["Detail.Unknown"] = "—",
        ["Detail.RssiUnknown"] = "Not measured",

        // Bluetooth Class of Device major class
        ["Class.0"] = "Miscellaneous",
        ["Class.1"] = "Computer",
        ["Class.2"] = "Phone",
        ["Class.3"] = "Network device",
        ["Class.4"] = "Audio device",
        ["Class.5"] = "Peripheral",
        ["Class.6"] = "Imaging device",
        ["Class.7"] = "Wearable",
        ["Class.8"] = "Toy",
        ["Class.9"] = "Medical device",
        ["Class.Unknown"] = "Unknown",

        // Tray menu
        ["Tray.Open"] = "Open main panel",
        ["Tray.DisconnectAll"] = "Disconnect all",
        ["Tray.Appearance"] = "Appearance",
        ["Tray.Theme.Auto"] = "Auto",
        ["Tray.Theme.Light"] = "Light",
        ["Tray.Theme.Dark"] = "Dark",
        ["Tray.Mica"] = "Mica background",
        ["Tray.MicaTip"] = "Blur layer showing the wallpaper behind the window",
        ["Tray.MicaUnsupported"] = "Requires Windows 11",
        ["Tray.Startup"] = "Start with Windows",
        ["Tray.Exit"] = "Exit",
        ["Tray.Language"] = "Language",
        ["Tray.Language.Zh"] = "中文",
        ["Tray.Language.En"] = "English",
        ["Tray.MenuTitle"] = "Tray menu",
        // The language's own name, as written in the log.
        ["Language.Name.Zh"] = "Chinese",
        ["Language.Name.En"] = "English",
        ["Tray.Tip.None"] = "BTAudioSink · Not connected",
        ["Tray.Tip.Count"] = "BTAudioSink · {0} connected",

        // Message box
        ["Msg.AlreadyRunning"] = "The app is already running. Open it from the tray icon.",
        ["Msg.LogOpenFailed"] = "Could not open the log file:\n{0}",

        // Runtime log
        ["Log.Started"] = "Started. Enumerating paired Bluetooth devices.",
        ["Log.MicaAutoDisabled"] = "The previous run ended abnormally while Mica was enabled; Mica has been disabled for this session to avoid rendering problems.",
        ["Log.AutoReconnectOn"] = "{0}: auto-reconnect enabled. The connection will be re-established after a disconnect.",
        ["Log.AutoReconnectOff"] = "{0}: auto-reconnect disabled.",
        ["Log.MicaOn"] = "Mica background enabled.",
        ["Log.MicaOff"] = "Mica background disabled.",
        ["Log.ThemeLight"] = "Appearance switched to light.",
        ["Log.ThemeDark"] = "Appearance switched to dark.",
        ["Log.ThemeAuto"] = "Appearance now follows the system ({0}).",
        ["Log.ThemeCurrent.Light"] = "light",
        ["Log.ThemeCurrent.Dark"] = "dark",
        ["Log.StartupOn"] = "Added to Windows startup.",
        ["Log.StartupOff"] = "Removed from Windows startup.",
        ["Log.DeviceFound"] = "Device found: {0}",
        ["Log.DeviceRemoved"] = "Device removed: {0}",
        ["Log.NoDevice"] = "No usable device found. Make sure Bluetooth is on and the device is paired.",
        ["Log.AutoConnectError"] = "Error during auto-connect: {0}",
        ["Log.AutoSkip"] = "All auto-reconnect devices are already connected or need no action; skipping.",
        ["Log.AutoStart"] = "{0}: {1} device(s) have auto-reconnect enabled. Checking Bluetooth link state…",
        ["Log.AutoReason.Startup"] = "Startup",
        ["Log.LinkNotEstablished"] = "{0}: the Bluetooth link is not established; the device is not connected to this PC.",
        ["Log.WakeAttempt"] = "{0}: Bluetooth link not established, trying to wake the device over the audio connection ({1}/{2})…",
        ["Log.WakeSuccess"] = "{0}: device woken, A2DP audio connection established.",
        ["Log.WakeFailedMax"] = "{0}: wake-up failed, the maximum number of attempts ({1}) has been reached; auto-connect is paused. Establish the Bluetooth link on the device, or click Connect manually.",
        ["Log.WakeRetry"] = "{0}: this wake-up attempt did not succeed; retrying in {1:0} second(s).",
        ["Log.LinkEstablished"] = "{0}: Bluetooth link established, starting the A2DP audio connection.",
        ["Log.LinkUnknown"] = "{0}: the Bluetooth link state could not be determined; trying the audio connection directly.",
        ["Log.AutoAttempt"] = "{0}: auto-connect attempt {1}/{2}…",
        ["Log.AutoSuccess"] = "{0}: auto-connect succeeded.",
        ["Log.AutoFailedMax"] = "{0}: auto-connect did not succeed after {1} consecutive attempts; retrying is paused. Re-establish the Bluetooth link or click Connect manually to try again.",
        ["Log.AutoConnectErrorDetail"] = "{0}: error during auto-connect: {1}",
        ["Log.DisconnectedSchedule"] = "{0}: the connection dropped; auto-reconnect starts in 2 seconds (up to {1} attempts)…",
        ["Log.LinkHint"] = "{0}: the Bluetooth link is not established; the audio connection will still be attempted.",
        ["Log.Disconnected"] = "Disconnected: {0}",
        ["Log.DisconnectFailed"] = "Failed to disconnect {0}: {1}",
        ["Log.DetailFailed"] = "Failed to read details for {0}: {1}",
        ["Log.Refresh"] = "Device list refreshed; rescanning ({0} disconnected entries removed).",
        ["Log.RefreshPlain"] = "Device list refreshed; rescanning.",
        ["Log.ReconnectDisconnecting"] = "Reconnecting {0}: dropping the current connection first",
        ["Log.Reconnected"] = "Reconnected: {0}",
        ["Log.ReconnectFailed"] = "Reconnect failed: {0}",
        ["Log.DisconnectAll"] = "Disconnected all devices ({0}).",
        ["Log.DisconnectAllNone"] = "No device was connected.",
        ["Log.LanguageChanged"] = "Interface language switched to {0}.",
        ["Log.ErrorDetail"] = "{0}: {1}",
        ["Log.SessionCreated"] = "Connection object created {0}: obj=0x{1:X8} id={2}",
        ["Log.NoneShort"] = "none",

        // Service log
        ["Log.ScanFailed"] = "Failed to start the Bluetooth scan: {0}",
        ["Log.ScanHint"] = "Make sure Bluetooth is on and the adapter supports the A2DP Sink role.",
        ["Log.RefreshWatcher"] = "Refreshing the device list: rebuilding DeviceWatcher",
        ["Log.StopWatcherFailed"] = "Failed to stop the device scan: {0}: {1}",
        ["Log.ConnectSkip"] = "Connecting {0} skipped: a connection object already exists (State={1}); the UI is synced to the actual state",
        ["Log.StillConnected"] = "{0}: the audio connection is still valid; the UI state has been corrected.",
        ["Log.StaleObject"] = "{0}: the connection object still exists but its state is {1}; it will be released and reconnected…",
        ["Log.ConnectStart"] = "Connecting {0}",
        ["Log.CreateFailed"] = "Failed to create the connection object: {0}",
        ["Log.NotSupported"] = "This device does not support the audio sink role (the Bluetooth adapter or driver may not support A2DP Sink).",
        ["Log.ConnectOk"] = "Connected {0} (State={1})",
        ["Log.ConnectFailed"] = "Failed to connect {0}: {1}",
        ["Log.ConnectError"] = "Error while connecting {0}: {1}: {2}",
        ["Log.DisconnectNoSession"] = "Disconnecting {0}: no connection object in _live, only the UI state is synced",
        ["Log.UnsubscribeFailed"] = "Failed to unsubscribe the state callback for {0}: {1}: {2}",
        ["Log.DisconnectTrace"] = "Disconnecting {0}: before={1} error={2} remaining={3} others[{4}] → after[{5}]",
        ["Log.DisconnectFailedDetail"] = "Failed to disconnect {0}: {1}",
        ["Log.CascadeCheck"] = "Cascade check: {0} is still Opened, nothing to restore",
        ["Log.CascadeRestore"] = "Cascade restore: the connection of {0} was closed by the system together, reopening it",
        ["Log.CascadeRestored"] = "{0}: the connection was closed by a system cascade and has been re-established automatically.",
        ["Log.ReleaseFailed"] = "Failed to release the connection: {0}: {1}",
        ["Log.StateCallbackReleased"] = "State callback (connection already released by this app, ignored) {0}",
        ["Log.StateCallbackCascade"] = "State callback {0} → {1} (system cascade, UI not updated)　others[{2}]",
        ["Log.StateCallback"] = "State callback {0} → {1}　others[{2}]",
        ["Log.DeviceRemovedSys"] = "Device removed (system notification): {0}",
        ["Log.ExitStart"] = "Exiting: cleaning up (closing audio connections, removing the tray icon)",
        ["Log.ExitDone"] = "Exiting: cleanup complete",
        ["Log.ExitFailed"] = "Cleanup failed while exiting: {0}: {1}",
        ["Log.UiUpdate"] = "UI update: {0} → {1} ({2})",
        ["Log.Calibrate"] = "Calibrating UI state: {0} {1} → {2}",
        ["Log.LinkQueryFailed"] = "Bluetooth link query failed: {0}: {1}",
        ["Log.DetailQueryFailed"] = "Failed to read device details: {0}: {1}",

        // Failure reasons
        ["Fail.Timeout"] = "The device did not respond in time. Confirm the connection on the device, or pick this PC from its Bluetooth device list.",
        ["Fail.Denied"] = "The connection request was denied by the system. Make sure Bluetooth is on and the device is still paired.",
        ["Fail.Unknown"] = "The system returned an unknown failure ({0}).",
        ["Fail.UnknownNoDetail"] = "The system returned an unknown failure with no further details.",
        ["Fail.Status"] = "Connection failed: {0}",
        ["Fail.ConnectionMayStillBeActive"] = "{0} (the connection may still be active)",
        ["Fail.ReadFailed"] = "Read failed ({0})",

        // Device
        ["Device.FallbackName"] = "Device#{0}",
    };

    /// <summary>当前语言。</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.Chinese;

    /// <summary>语言切换时触发。界面据此刷新那些从代码里取的文案。</summary>
    public static event Action? LanguageChanged;

    private static Dictionary<string, string> Table =>
        Language == AppLanguage.English ? En : Zh;

    /// <summary>取一条文案。键不存在时返回键本身，便于一眼看出漏了哪条。</summary>
    public static string Get(string key) =>
        Table.TryGetValue(key, out var value) ? value : key;

    /// <summary>取一条带占位符的文案并填参数。</summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 占位符和参数对不上时别让程序崩，原样返回模板即可。
            return template;
        }
    }

    /// <summary>切换语言并通知界面刷新。</summary>
    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// 把当前语言的文案铺进一份资源字典，供 XAML 用 <c>{DynamicResource 键}</c> 取。
    /// </summary>
    public static ResourceDictionary BuildResourceDictionary()
    {
        var dictionary = new ResourceDictionary();
        foreach (var pair in Table)
        {
            dictionary[pair.Key] = pair.Value;
        }
        return dictionary;
    }
}
