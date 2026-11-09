using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTAudioSink.Models;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Audio;

namespace BTAudioSink.Services;

/// <summary>
/// 封装蓝牙 A2DP Sink 的连接管理。窗口层只订阅事件，不直接碰 WinRT。
/// 事件都在后台线程触发，订阅方自行切到 UI 线程。
///
/// 两条铁律：
/// 1. <b>AudioPlaybackConnection 一旦 Dispose()，就再也不能碰它</b>——包括读 State。
///    投影对象里的原生指针已释放，再读就是野指针访问，进程以 0xC0000005 直接终止，
///    try/catch 拦不住（所以崩溃时 crash.log 是空的）。判定断开成功只能看 Dispose 有没有抛异常。
/// 2. 所有对 _live 的读写、以及「取对象 + 读 State」这一对动作，都必须在 _gate 里完成。
///    界面线程问状态的同时，设备监听线程可能正在释放同一个对象。
/// </summary>
public sealed class AudioSinkService : IDisposable
{
    /// <summary>一台设备的一次连接。把事件处理器单独存下来，才能在建/拆时成对订阅与退订。</summary>
    private sealed class Session
    {
        public required string Id { get; init; }

        public required AudioPlaybackConnection Connection { get; init; }

        public TypedEventHandler<AudioPlaybackConnection, object>? Handler { get; set; }

        /// <summary>
        /// 原生对象是否已经 Dispose。<b>Dispose 之后再读 State 会让进程直接崩溃</b>
        /// （野指针访问，try/catch 拦不住），所以任何要读 State 的地方都得先问这一句。
        /// </summary>
        public bool Released { get; set; }
    }

    private readonly object _gate = new();

    /// <summary>
    /// 所有 OpenAsync 都经过同一条队列。系统会把并发建立的音频连接互相牵连，
    /// 导致另一台被报为 Closed / UnknownFailure；手动、自动和联动恢复不能各连各的。
    /// </summary>
    private readonly SemaphoreSlim _openGate = new(1, 1);

    private readonly Dictionary<string, Session> _live = new();
    private readonly Dictionary<string, string> _names = new();

    /// <summary>
    /// 设备接口 ID → Windows 容器 ID。蓝牙设备（BluetoothDevice）和它的 A2DP 接口共用同一个容器 ID，
    /// 这是把「音频接口」和「蓝牙链路状态」对上号的唯一可靠办法。
    /// </summary>
    private readonly Dictionary<string, Guid> _containers = new();

    /// <summary>正在被自动恢复的设备：这段时间内它收到的 Closed 是系统连带产生的，不要拿去刷界面。</summary>
    private readonly HashSet<string> _restoring = new();

    /// <summary>正在执行「全部断开」，此时不搞恢复。</summary>
    private bool _disconnectingAll;

    private DeviceWatcher? _watcher;
    private bool _disposed;

    /// <summary>id, name</summary>
    public event Action<string, string>? DeviceFound;

    public event Action<string>? DeviceLost;

    /// <summary>id, 连接状态, 状态枚举, 补充说明（可空）</summary>
    public event Action<string, bool, DeviceStatus, string?>? ConnectionChanged;

    /// <summary>枚举结束（可能一个设备都没有）</summary>
    public event Action? EnumerationCompleted;

    /// <summary>只发给界面；文件日志由窗口的 AppendLog 统一落盘，避免重复写入。</summary>
    public event Action<string>? Log;

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            _watcher.Added += OnAdded;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Report(Loc.Format("Log.ScanFailed", ex.Message));
            Report(Loc.Get("Log.ScanHint"));
        }
    }

    /// <summary>
    /// 重新枚举设备。DeviceWatcher 一旦 Stop 就不能再 Start，只能整只换新的。
    /// 已建立的连接不受影响——刷新只重建「发现设备」这条链路。
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;

        AppLog.Write(Loc.Get("Log.RefreshWatcher"));
        StopWatcher();
        Start();
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;

        try
        {
            // 先退订再停：停的过程中可能还派发事件，别让回调打到正在换掉的 watcher 上。
            _watcher.Added -= OnAdded;
            _watcher.Removed -= OnRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.Format("Log.StopWatcherFailed", ex.GetType().Name, ex.Message));
        }
        finally
        {
            _watcher = null;
        }
    }

    private void Report(string message) => Log?.Invoke(message);

    public bool IsConnected(string deviceId)
    {
        lock (_gate)
        {
            return _live.ContainsKey(deviceId);
        }
    }

    /// <summary>连接对象存在<b>并且</b>状态确实是 Opened。界面问状态就该问这个，别猜。</summary>
    public bool IsOpened(string deviceId)
    {
        lock (_gate)
        {
            return _live.TryGetValue(deviceId, out var session) &&
                   ReadState(session).Equals("Opened", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 问一次「这台设备的蓝牙链路现在通不通」。
    /// 设备列表里出现只代表<b>配对过</b>，不代表蓝牙连着——自动连接之前必须先问这一句，
    /// 否则会在一个根本连不上的设备上反复等到超时。
    /// </summary>
    public Task<BluetoothLinkState> QueryLinkAsync(string deviceId)
    {
        Guid? container;
        lock (_gate)
        {
            container = _containers.TryGetValue(deviceId, out var id) ? id : null;
        }

        return BluetoothLink.QueryAsync(container, deviceId);
    }

    /// <summary>读一台设备的详细信息（地址、信号、上次连接）。查不到返回 null。</summary>
    public Task<BluetoothLinkDetails?> QueryDetailsAsync(string deviceId)
    {
        Guid? container;
        lock (_gate)
        {
            container = _containers.TryGetValue(deviceId, out var id) ? id : null;
        }

        return BluetoothLink.QueryDetailsAsync(container, deviceId);
    }

    public async Task<bool> ConnectAsync(string deviceId)
    {
        if (_disposed) return false;

        await _openGate.WaitAsync();
        try
        {
            return await ConnectCoreAsync(deviceId);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task<bool> ConnectCoreAsync(string deviceId)
    {
        if (_disposed) return false;

        Session? existing;
        lock (_gate)
        {
            _live.TryGetValue(deviceId, out existing);
        }

        if (existing is not null)
        {
            // 界面显示的「未连接」可能已经过期（典型场景：断另一台时系统牵连报了 Closed）。
            // 这里绝不能默默 return —— 否则界面会永远停在「正在连接…」。
            var realState = ReadState(existing);
            var opened = realState.Equals("Opened", StringComparison.OrdinalIgnoreCase);
            AppLog.Write(Loc.Format("Log.ConnectSkip", Display(deviceId), realState));

            if (opened)
            {
                Report(Loc.Format("Log.StillConnected", Display(deviceId)));
                ConnectionChanged?.Invoke(deviceId, true, DeviceStatus.Connected, null);
                return true;
            }

            // 对象还在但已经不是 Opened：先拆干净，再走完整重连。
            Report(Loc.Format("Log.StaleObject", Display(deviceId), realState));
            Drop(deviceId);
        }

        Report(Loc.Format("Log.ConnectStart", Display(deviceId)));

        AudioPlaybackConnection? connection;
        try
        {
            connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
        }
        catch (Exception ex)
        {
            Report(Loc.Format("Log.CreateFailed", ex.Message));
            return false;
        }

        if (connection is null)
        {
            Report(Loc.Get("Log.NotSupported"));
            return false;
        }

        var session = new Session { Id = deviceId, Connection = connection };
        // 处理器必须留引用：退订时要求是同一个实例。
        session.Handler = (_, _) => HandleStateChanged(deviceId);
        connection.StateChanged += session.Handler;

        lock (_gate)
        {
            if (_disposed)
            {
                connection.StateChanged -= session.Handler;
                TryDispose(connection);
                return false;
            }
            _live[deviceId] = session;
        }

        AppLog.Write(Loc.Format("Log.SessionCreated", Display(deviceId),
            RuntimeHelpers.GetHashCode(connection), deviceId));

        try
        {
            // 两步：先启用接收角色，再真正建立连接。
            await connection.StartAsync();
            var result = await connection.OpenAsync();

            if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
            {
                Report(Loc.Format("Log.ConnectOk", Display(deviceId), ReadState(session)));
                ConnectionChanged?.Invoke(deviceId, true, DeviceStatus.Connected, null);
                return true;
            }

            Report(Loc.Format("Log.ConnectFailed", Display(deviceId), DescribeFailure(result)));
            Drop(deviceId);
            ConnectionChanged?.Invoke(deviceId, false, DeviceStatus.ConnectFailed, null);
            return false;
        }
        catch (Exception ex)
        {
            Report(Loc.Format("Log.ConnectError", Display(deviceId), ex.GetType().Name, ex.Message));
            Drop(deviceId);
            ConnectionChanged?.Invoke(deviceId, false, DeviceStatus.ConnectFailed, null);
            return false;
        }
    }

    /// <summary>
    /// 断开一台设备。返回值反映「真实结果」：Dispose 抛异常时返回 false，界面显示失败，
    /// 而不是先把界面改成未连接、底层却还开着。
    ///
    /// 判定依据只有「Dispose 有没有抛异常」——关闭后再读 State 会让进程崩溃。
    /// </summary>
    public bool Disconnect(string deviceId, out string detail)
    {
        Session? session;
        string before;
        string othersBefore;
        List<string> othersOpened;

        lock (_gate)
        {
            if (!_live.Remove(deviceId, out session))
            {
                detail = Loc.Get("Status.Disconnected");
                AppLog.Write(Loc.Format("Log.DisconnectNoSession", Display(deviceId)));
                ConnectionChanged?.Invoke(deviceId, false, DeviceStatus.Disconnected, null);
                return true;
            }

            // 只在 Dispose 之前读状态。
            before = ReadState(session);
            othersBefore = SnapshotLocked(deviceId);
            // 记下此刻还开着的其它设备：系统会在下面 Dispose 时把它们一起报成 Closed。
            othersOpened = _live
                .Where(pair => ReadState(pair.Value).Equals("Opened", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            // 立标记：从这一刻起谁都不许再读这个对象的状态。
            // 必须和上面的读取放在同一把锁里，否则可能刚读完就被另一条线程释放掉。
            session.Released = true;
        }

        if (session.Handler is not null)
        {
            try
            {
                session.Connection.StateChanged -= session.Handler;
            }
            catch (Exception ex)
            {
                AppLog.Write(Loc.Format("Log.UnsubscribeFailed", Display(deviceId), ex.GetType().Name, ex.Message));
            }
        }

        // 互斥点：从这一刻到恢复完成，其它设备收到的 Closed 是系统连带产生的，不要拿去刷界面。
        if (!_disconnectingAll && othersOpened.Count > 0)
        {
            lock (_gate)
            {
                foreach (var id in othersOpened) _restoring.Add(id);
            }
        }

        string? error = null;
        try
        {
            // WinRT 的 IClosable.Close() 在 C# 投影里就是 Dispose()。
            // 文档：底层传输在引用全部释放后才会真正停掉。
            session.Connection.Dispose();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        // 关键埋点：断开一台后，另一台的状态有没有被系统牵连。
        string othersAfter;
        int remaining;
        lock (_gate)
        {
            othersAfter = SnapshotLocked(deviceId);
            remaining = _live.Count;
        }

        AppLog.Write(
            Loc.Format("Log.DisconnectTrace", Display(deviceId), before, error ?? "-", remaining,
                othersBefore, othersAfter));

        if (othersOpened.Count > 0)
        {
            _ = RestoreCascadeAsync(othersOpened);
        }

        if (error is not null)
        {
            detail = Loc.Format("Fail.ConnectionMayStillBeActive", error);
            Report(Loc.Format("Log.DisconnectFailedDetail", Display(deviceId), detail));
            ConnectionChanged?.Invoke(deviceId, true, DeviceStatus.DisconnectFailed, detail);
            return false;
        }

        detail = Loc.Get("Status.Disconnected");
        ConnectionChanged?.Invoke(deviceId, false, DeviceStatus.Disconnected, null);
        return true;
    }

    /// <summary>返回真正断开成功的台数。</summary>
    public int DisconnectAll()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = new List<string>(_live.Keys);
        }

        _disconnectingAll = true;
        try
        {
            var closed = 0;
            foreach (var id in ids)
            {
                if (Disconnect(id, out _))
                {
                    closed++;
                }
            }
            return closed;
        }
        finally
        {
            _disconnectingAll = false;
            lock (_gate)
            {
                _restoring.Clear();
            }
        }
    }

    /// <summary>
    /// 断开一台会把同一适配器上其它设备的连接状态一起打成 Closed（系统行为，实测确认），
    /// 但另一台的音频流并不一定停。这里把被牵连的设备重新打开，恢复成一个有效句柄：
    /// 界面上它保持「已连接」，之后想断它也能真的断掉。
    /// </summary>
    private async Task RestoreCascadeAsync(List<string> ids)
    {
        // 给系统一点时间把状态稳定下来，避免刚报 Closed 就被我们重开、又被它改回去。
        await Task.Delay(700);

        foreach (var id in ids)
        {
            bool opened;
            bool alive;
            lock (_gate)
            {
                alive = _live.TryGetValue(id, out var session);
                opened = alive && ReadState(session!).Equals("Opened", StringComparison.OrdinalIgnoreCase);
            }

            lock (_gate)
            {
                _restoring.Remove(id);
            }

            if (!alive)
            {
                continue;
            }

            if (opened)
            {
                AppLog.Write(Loc.Format("Log.CascadeCheck", Display(id)));
                // 之前为了不刷界面而压下的状态，这里补一次真实回推。
                ConnectionChanged?.Invoke(id, true, DeviceStatus.Connected, null);
                continue;
            }

            AppLog.Write(Loc.Format("Log.CascadeRestore", Display(id)));
            Report(Loc.Format("Log.CascadeRestored", Display(id)));

            Drop(id);
            await ConnectAsync(id);
        }
    }

    /// <summary>
    /// 读连接状态。<b>对象已经 Dispose 过就不能再读</b>——原生指针已释放，
    /// 读它会让进程以 0xC0000005 终止，try/catch 拦不住。
    /// 所以每个 Session 都带一个 Released 标记，释放前先立起来，之后一律不再碰它。
    /// </summary>
    private static string ReadState(Session session)
    {
        if (session.Released) return "Released";

        try
        {
            return session.Connection.State.ToString();
        }
        catch (Exception ex)
        {
            return Loc.Format("Fail.ReadFailed", ex.GetType().Name);
        }
    }

    /// <summary>连接失败了：先退订再释放，避免回调打到自己身上。</summary>
    private void Drop(string deviceId)
    {
        Session? session;
        lock (_gate)
        {
            if (!_live.Remove(deviceId, out session)) return;
            // 标记放在锁里：所有读状态的地方也都持有同一把锁，
            // 保证「立标记」和「读状态」不会交叉，释放后不可能还有人在读。
            session.Released = true;
        }

        if (session.Handler is not null)
        {
            try
            {
                session.Connection.StateChanged -= session.Handler;
            }
            catch
            {
                // 退订失败不影响释放。
            }
        }

        TryDispose(session.Connection);
    }

    private static void TryDispose(AudioPlaybackConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.Format("Log.ReleaseFailed", ex.GetType().Name, ex.Message));
        }
    }

    private void HandleStateChanged(string deviceId)
    {
        if (_disposed) return;

        string state;
        string others;
        lock (_gate)
        {
            // 已被本程序处置的连接不会再出现在 _live 里；此时绝不能去读它的 State。
            if (!_live.TryGetValue(deviceId, out var session))
            {
                AppLog.Write(Loc.Format("Log.StateCallbackReleased", Display(deviceId)));
                return;
            }

            state = ReadState(session);
            // 连另一台的状态一起记：能直接看出「断一台、另一台跟着变」是系统行为还是我们的 bug。
            others = SnapshotLocked(deviceId);
        }

        var opened = state.Equals("Opened", StringComparison.OrdinalIgnoreCase);

        bool restoring;
        lock (_gate)
        {
            restoring = _restoring.Contains(deviceId);
        }

        if (restoring)
        {
            // 这一条是「断另一台」的连带反应，设备其实还在响；别拿去把界面刷成未连接。
            AppLog.Write(Loc.Format("Log.StateCallbackCascade", Display(deviceId), state, others));
            return;
        }

        AppLog.Write(Loc.Format("Log.StateCallback", Display(deviceId), state, others));
        ConnectionChanged?.Invoke(deviceId, opened,
            opened ? DeviceStatus.Connected : DeviceStatus.Disconnected, null);
    }

    /// <summary>把除 excludeId 之外所有活着的连接的状态拼成一行，供日志对比。调用方需持有 _gate。</summary>
    private string SnapshotLocked(string excludeId)
    {
        if (_live.Count <= 1) return Loc.Get("Log.NoneShort");

        var parts = new List<string>(_live.Count);
        foreach (var pair in _live)
        {
            if (pair.Key == excludeId) continue;
            parts.Add($"{Display(pair.Key)}={ReadState(pair.Value)}");
        }
        return parts.Count == 0 ? Loc.Get("Log.NoneShort") : string.Join(", ", parts);
    }

    private static string DescribeFailure(AudioPlaybackConnectionOpenResult result) => result.Status switch
    {
        AudioPlaybackConnectionOpenResultStatus.RequestTimedOut
            => Loc.Get("Fail.Timeout"),
        AudioPlaybackConnectionOpenResultStatus.DeniedBySystem
            => Loc.Get("Fail.Denied"),
        AudioPlaybackConnectionOpenResultStatus.UnknownFailure
            => DescribeUnknownFailure(result),
        _ => Loc.Format("Fail.Status", result.Status),
    };

    private static string DescribeUnknownFailure(AudioPlaybackConnectionOpenResult result)
    {
        if (result.ExtendedError is not { } error)
        {
            return Loc.Get("Fail.UnknownNoDetail");
        }

        var hresult = $"HRESULT=0x{error.HResult:X8}";
        return string.IsNullOrWhiteSpace(error.Message)
            ? Loc.Format("Fail.Unknown", hresult)
            : Loc.Format("Fail.Unknown", $"{error.Message} ({hresult})");
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation device)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name;
        lock (_gate)
        {
            _names[device.Id] = name;

            if (device.Properties.TryGetValue("System.Devices.ContainerId", out var raw) && raw is Guid container)
            {
                _containers[device.Id] = container;
            }
        }
        DeviceFound?.Invoke(device.Id, name);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        AppLog.Write(Loc.Format("Log.DeviceRemovedSys", Display(update.Id)));
        lock (_gate)
        {
            _containers.Remove(update.Id);
        }
        // 设备被拔出/取消配对时，连接对象同样要安全释放。
        Drop(update.Id);
        DeviceLost?.Invoke(update.Id);
        ConnectionChanged?.Invoke(update.Id, false, DeviceStatus.Removed, null);
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        EnumerationCompleted?.Invoke();
    }

    /// <summary>
    /// 日志里用的设备标识。<b>不能用 id 尾部</b>：所有 A2DP Sink 设备的 id 都以
    /// 「{6994ad04-...-00a0c9223196}\SNK」结尾，尾部完全相同，两台设备在日志里分辨不出来。
    /// 所以用「设备名 + id 哈希前 6 位」。
    /// </summary>
    private string Display(string id)
    {
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..6];
        string? name;
        lock (_gate)
        {
            _names.TryGetValue(id, out name);
        }
        return name is null ? Loc.Format("Device.FallbackName", tag) : $"{name}#{tag}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopWatcher();

        List<string> ids;
        lock (_gate)
        {
            ids = new List<string>(_live.Keys);
        }

        foreach (var id in ids)
        {
            Drop(id);
        }
    }
}
